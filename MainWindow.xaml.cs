// MainWindow.xaml.cs
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using HachBobAI.Config;
using HachBobAI.Input;
using HachBobAI.Vision;

namespace HachBobAI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private AppConfig      _cfg     = new();
    private MouseLogic     _mouse   = new();
    private VisionEngine?  _vision;
    private OverlayWindow? _overlay;
    private IndicatorWindow? _indicator;

    private List<PresetConfig> _presets      = [];
    private int _activePreset = -1;
    public int ActivePresetIndex
    {
        get => _activePreset;
        private set
        {
            if (_activePreset == value) return;
            _activePreset = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivePresetIndex)));
        }
    }

    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _dirty;

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        _dirty = true;
    }

    private string _status = "ACTIVE";
    public  string  Status  { get => _status; set => Set(ref _status, value); }

    public VisionConfig      VisionCfg => _cfg.Vision;
    public TriggerbotConfig  TbCfg     => _cfg.Triggerbot;
    public BindsConfig       Binds     => _cfg.Binds;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _cfg     = ConfigManager.Load();
        _presets = ConfigManager.LoadPresets() ?? _cfg.Presets;
        // ★ Восстанавливаем активный пресет из конфига
        _activePreset = _cfg.ActivePreset;
        if (_activePreset >= _presets.Count) _activePreset = -1;

        ApplyConfigToUi();
        InitVision();
        InitMouse();
        InitIndicator();

        _autoSaveTimer.Tick += (_, _) => { if (_dirty) { DoSave(); _dirty = false; } };
        _autoSaveTimer.Start();

        // Сброс DataContext перечитывает все вложенные биндинги (VisionConfig и т.д.)
        _initUi = true;
        DataContext = null;
        DataContext = this;
        _initUi = false;
        BindsControl.Binds               = null; BindsControl.Binds       = _cfg.Binds;
        DeadZonesControl.Groups          = null;
        DeadZonesControl.Groups          = _cfg.DeadZoneGroups;
        DeadZonesControl.ActiveGroupIndex = _cfg.ActiveDeadZoneGroup;
        // ★ Явно передаём пресеты — {Binding _presets} не работает (приватное поле)
        PresetsControl.Presets       = null;
        PresetsControl.Presets       = _presets;
        PresetsControl.ActiveIndex   = _activePreset;
        PresetsControl.PresetChanged += OnPresetCardChanged;

        // ★ Принудительно применяем зоны текущей группы после инициализации UI
        if (_vision != null)
        {
            var zones = _cfg.CurrentZones;
            _vision.DeadZones       = zones.Select(d => new DeadZone(d)).ToList();
            _vision.DeadZoneEnabled = _cfg.Vision.DeadZone;
            Console.WriteLine($"[dz] Init: группа={_cfg.CurrentGroupName}, зон={zones.Count}, enabled={_cfg.Vision.DeadZone}");
            _overlay?.UpdateFov();
        }

        Topmost  = true;
        Closing += OnWindowClosing;
    }

    private bool _initUi; // блокирует OnProviderChanged во время инициализации

    // Не все элементы UI обязаны иметь generated-field из XAML. Если x:Name/поле
    // отсутствует, ищем элемент по runtime name и не валим сборку code-behind.
    private T? Ui<T>(string name) where T : FrameworkElement => FindName(name) as T;

    private void ApplyConfigToUi()
    {
        _initUi = true;
        try
        {
            // Синхронизируем ComboBox провайдера из конфига
            var prov = _cfg.Vision.Provider.ToLowerInvariant();
            foreach (ComboBoxItem item in ProviderCombo.Items)
                if (item.Tag?.ToString() == prov) { ProviderCombo.SelectedItem = item; break; }
            if (ProviderCombo.SelectedItem == null) ProviderCombo.SelectedIndex = 0;

            // Важно: синхронизируем RadioButton под _initUi=true, иначе дефолтный Checked
            // из XAML может перезаписать config обратно в use_tiled=true.
            SyncCaptureModeUi(_cfg.Vision.UseTiled);

            CaptureSizeRow.Visibility = Visibility.Visible;
            UpdateCaptureSizeHint(_cfg.Vision.CaptureSize, _cfg.Vision.UseTiled);

            // Синхронизируем поля позиции индикатора
            if (Ui<TextBox>("IndXBox") is { } indXBox)
                indXBox.Text = (_cfg.IndicatorX >= 0 ? _cfg.IndicatorX : 20).ToString();
            if (Ui<TextBox>("IndYBox") is { } indYBox)
                indYBox.Text = (_cfg.IndicatorY >= 0 ? _cfg.IndicatorY : 20).ToString();
            if (Ui<CheckBox>("IndEnabledCheck") is { } indEnabledCheck)
                indEnabledCheck.IsChecked = _cfg.IndicatorEnabled;
            RefreshPresetSidebar();
        }
        finally
        {
            _initUi = false;
        }
    }

    private void SyncCaptureModeUi(bool tiled)
    {
        if (Ui<RadioButton>("ModeTiled") is { } modeTiled)
            modeTiled.IsChecked = tiled;

        // В твоём XAML обычный режим называется ModeSquish ("Сжатие").
        string[] normalNames = ["ModeSquish", "ModeNormal", "ModeSingle", "ModeCompress", "ModeCompressed", "ModeResize"];
        foreach (string name in normalNames)
            if (Ui<RadioButton>(name) is { } rb)
                rb.IsChecked = !tiled;
    }

    private void InitVision()
    {
        var vs = _cfg.Vision;
        if (!vs.Enabled) { Console.WriteLine("[vision] Disabled"); return; }
        if (!File.Exists(vs.ModelPath)) { Console.WriteLine($"[vision] Model not found: {vs.ModelPath}"); return; }

        // ★ FIX: TRT кэш пишется/читается относительно CWD — ставим CWD рядом с моделью
        // чтобы trt_cache сохранялся там же при повторных dotnet run
        string? modelDir = Path.GetDirectoryName(Path.GetFullPath(vs.ModelPath));
        if (!string.IsNullOrEmpty(modelDir) && Directory.Exists(modelDir))
        {
            Directory.SetCurrentDirectory(modelDir);
            Console.WriteLine($"[vision] CWD → {modelDir}");
        }

        try
        {
            int sw = (int)SystemParameters.PrimaryScreenWidth;
            int sh = (int)SystemParameters.PrimaryScreenHeight;
            _vision = new VisionEngine(vs.ModelPath, sw, sh,
                (float)vs.Conf, vs.FovRadius, vs.Provider, vs.CaptureSize, vs.UseFp16,
                vs.NumaPinning, vs.NumaCores,
                vs.DmlDeviceId);
            _vision.AimYOffsetPercent = (float)vs.AimYOffset;
            _vision.SetDetectionClass(vs.DetectionClass);
            _vision.PredictionStr   = (float)vs.Prediction;
            _vision.ConfirmFrames   = vs.ConfirmFrames;
            _vision.StopDist        = (float)vs.StopDist;
            _vision.DeadZoneEnabled = vs.DeadZone;
            _vision.DeadZones       = _cfg.CurrentZones.Select(d => new DeadZone(d)).ToList();
            _vision.PrioritySize    = vs.PrioritySize;
            _vision.GhostMaxFrames  = vs.GhostMaxFrames;
            _vision.AimHz           = vs.AimHz;
            _vision.UseTiled        = vs.UseTiled;
            _vision.TileOverlap     = vs.TileOverlap;
            if (vs.UseTiled) _vision.SetCaptureSize(vs.CaptureSize);
            _vision.Start();
            Console.WriteLine($"[vision] Started — {_vision.ProviderName}");
            _overlay = new OverlayWindow(_vision);
            if (vs.ShowFov) _overlay.Show();
            UpdateClassUi();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[vision] FAILED ({vs.Provider}): {ex.Message}");
            if (ex.InnerException != null) Console.WriteLine($"         {ex.InnerException.Message}");
        }
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initUi) return; // не трогаем конфиг пока грузится UI
        if (ProviderCombo.SelectedItem is ComboBoxItem item)
        {
            _cfg.Vision.Provider = item.Tag?.ToString() ?? "cpu";
            _dirty = true;
            Console.WriteLine($"[vision] Провайдер → {_cfg.Vision.Provider} (нажми ↺ Рестарт)");
        }
    }

    private void OnBrowseModel(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Модели|*.onnx;*.xml|ONNX (.onnx)|*.onnx|OpenVINO IR (.xml)|*.xml|Все файлы|*.*",
            Title  = "Выбери модель",
        };
        if (File.Exists(_cfg.Vision.ModelPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_cfg.Vision.ModelPath);
        if (dlg.ShowDialog() == true)
        {
            _cfg.Vision.ModelPath = dlg.FileName;
            ModelPathBox.Text     = dlg.FileName;

            // Автовыбор провайдера: XML-модель → OpenVINO
            if (dlg.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                _cfg.Vision.Provider = "openvino";
                foreach (ComboBoxItem ci in ProviderCombo.Items)
                    if (ci.Tag?.ToString() == "openvino") { ProviderCombo.SelectedItem = ci; break; }
                Console.WriteLine("[vision] XML модель → провайдер переключён на OpenVINO");
            }

            _dirty = true;
        }
    }

    private async void OnRestartVision(object sender, RoutedEventArgs e)
    {
        // Блокируем кнопку на время рестарта — двойной клик = гарантированный краш
        var btn = sender as Button;
        if (btn != null) { btn.IsEnabled = false; btn.Content = "⏳ Рестарт..."; }

        if (ProviderCombo.SelectedItem is ComboBoxItem item)
            _cfg.Vision.Provider = item.Tag?.ToString() ?? _cfg.Vision.Provider;
        if (!string.IsNullOrWhiteSpace(ModelPathBox.Text))
            _cfg.Vision.ModelPath = ModelPathBox.Text;

        _mouse.Vision = null;
        _overlay?.Close();
        _overlay = null;

        // Stop() ждёт завершения потоков (до 2 сек) и Dispose сессии — делаем в пуле
        var oldVision = _vision;
        _vision = null;
        await Task.Run(() => oldVision?.Stop());

        InitVision();
        _mouse.Vision = _vision;
        _dirty = true;
        DoSave();

        if (btn != null) { btn.IsEnabled = true; btn.Content = "↺ Рестарт"; }
    }


    private void InitMouse()
    {
        _mouse.Vision      = _vision;
        _mouse.Strength    = (float)_cfg.Vision.Strength;
        _mouse.MaxStep     = (float)_cfg.Vision.MaxStep;
        _mouse.Smooth      = (float)_cfg.Vision.Smooth;
        _mouse.AimHz       = _cfg.Vision.AimHz;
        _mouse.ToggleMode  = _cfg.ToggleMode;
        _mouse.TbEnabled   = _cfg.Triggerbot.Enabled;
        _mouse.TbTolerance = (float)_cfg.Triggerbot.Tolerance;
        _mouse.TbDelayMin  = (float)_cfg.Triggerbot.DelayMin / 1000f;
        _mouse.TbDelayMax  = (float)_cfg.Triggerbot.DelayMax / 1000f;
        _mouse.TbTargetSwitchDelayMs  = (float)_cfg.Triggerbot.TargetSwitchDelayMs;
        _mouse.TbTargetSwitchRadiusPx = (float)_cfg.Triggerbot.TargetSwitchRadiusPx;
        _mouse.TbAimOnly   = _cfg.Triggerbot.AimOnly;

        SyncBinds();
        SyncPresetBinds();

        _mouse.OnToggleEnabled = enabled => Dispatcher.Invoke(() =>
        {
            Status = enabled ? "ACTIVE" : "PAUSED";
            StatusLabel.Foreground = new SolidColorBrush(enabled
                ? Color.FromRgb(0, 255, 157) : Color.FromRgb(255, 85, 85));
        });
        _mouse.OnShowOverlay      = () => Dispatcher.Invoke(() => _overlay?.Toggle());
        _mouse.OnHideGui          = () => Dispatcher.Invoke(ToggleGui);
        _mouse.OnDeadZoneToggle   = () => Dispatcher.Invoke(NextDeadZoneGroup);
        _mouse.OnTriggerbotToggle = () => Dispatcher.Invoke(() =>
        {
            _cfg.Triggerbot.Enabled = _mouse.TbEnabled;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TbCfg)));
        });
        _mouse.OnDetectionClassToggle = cls => Dispatcher.Invoke(() =>
        {
            _cfg.Vision.DetectionClass = cls;
            UpdateClassUi();
            _dirty = true;
        });
        _mouse.OnRangefinderToggle = () => Dispatcher.Invoke(() =>
        {
            _cfg.Rangefinder.Enabled = _mouse.RfEnabled;
        });
        _mouse.OnPresetApply = idx => Dispatcher.Invoke(() => ApplyPreset(idx));
        _mouse.OnExit        = () => Dispatcher.Invoke(DoExit);

        if (_activePreset >= 0 && _activePreset < _presets.Count)
        {
            int saved = _activePreset;
            _activePreset = -1; // временно сбрасываем, иначе ApplyPreset его выключит
            ApplyPreset(saved);
        }

        _mouse.Start();
    }

    private void InitIndicator()
    {
        try
        {
            _indicator = new IndicatorWindow();
            _indicator.Show(); // сначала показываем — окно получает HWND

            // Позиция из конфига; если не задана — левый верхний угол
            int ix = _cfg.IndicatorX >= 0 ? _cfg.IndicatorX : 20;
            int iy = _cfg.IndicatorY >= 0 ? _cfg.IndicatorY : 20;
            _indicator.SetPosition(ix, iy);

            // Скрываем если выключен в конфиге
            if (!_cfg.IndicatorEnabled) _indicator.Hide();
        }
        catch (Exception ex) { Console.WriteLine($"[main] Indicator: {ex.Message}"); }

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        t.Tick += (_, _) => PollIndicator();
        t.Start();
    }

    private string _lastIndState  = "";
    private string _lastIndPreset = "";
    private void PollIndicator()
    {
        if (_indicator == null) return;
        bool tbOn    = _mouse.TbEnabled;
        bool onTgt   = _vision?.IsOnTarget ?? false;
        bool aimHeld = _mouse.AimHeld;

        string state =
            tbOn && onTgt                         ? "FIRE" :
            tbOn && _mouse.TbAimOnly && aimHeld   ? "AIM"  :
            tbOn                                  ? "ON"   : "OFF";

        string preset = _activePreset >= 0 && _activePreset < _presets.Count
            ? _presets[_activePreset].Name : "";

        string bind = _activePreset >= 0 && _activePreset < _presets.Count
            ? (_presets[_activePreset].Bind?.ToUpper() ?? "") : "";
        // FPS display
        if (_vision != null)
            FpsLabel.Content = $"{_vision.Fps:F0} FPS  [{_vision.ProviderName}]  [{_vision.ActiveClassName}]";
        else
        {
            System.IO.File.AppendAllText("debug.log",
                $"{DateTime.Now:HH:mm:ss} [POLL] _vision is NULL\n");
        }
        // ★ Обновляем если изменился state ИЛИ пресет
        if (state != _lastIndState || preset != _lastIndPreset)
        {
            _lastIndState  = state;
            _lastIndPreset = preset;
            _indicator.UpdateState(state, preset, bind);
        }

        // FPS display
        if (_vision != null)
            FpsLabel.Content = $"{_vision.Fps:F0} FPS  [{_vision.ProviderName}]  [{_vision.ActiveClassName}]";
    }

    // ── Presets ───────────────────────────────────────────────────────────────
    private void RefreshPresetSidebar()
    {
        PresetPanel.Children.Clear();
        for (int i = 0; i < _presets.Count; i++)
        {
            var p      = _presets[i];
            bool isAct = i == _activePreset;
            var colors = new[] { "#3A7BD5","#00CC66","#FF8C00","#FF4B4B","#AA44FF" };
            string col = colors[i % colors.Length];

            var btn = new Button
            {
                Content         = $"{p.Bind?.ToUpper() ?? "—"}  {p.Name}",
                Height          = 24,
                Margin          = new Thickness(2, 1, 2, 1),
                Background      = isAct
                    ? (Brush)new BrushConverter().ConvertFrom(col)!
                    : new SolidColorBrush(Color.FromRgb(26, 26, 42)),
                Foreground      = isAct
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                BorderThickness = new Thickness(1),
                FontFamily      = new FontFamily("Consolas"),
                FontSize        = 10,
                Tag             = i,
            };
            var idx = i;
            btn.Click += (_, _) => ApplyPreset(idx);
            PresetPanel.Children.Add(btn);
        }
        ActivePresetLabel.Content = _activePreset >= 0 && _activePreset < _presets.Count
            ? _presets[_activePreset].Name : "—";
    }

    private void ApplyPreset(int idx)
    {
        if (idx < 0 || idx >= _presets.Count) return;
        if (_activePreset == idx) { DeactivatePreset(); return; }

        var p = _presets[idx];
        ActivePresetIndex = idx;

        _cfg.Vision.FovRadius     = (int)p.FovRadius;
        _cfg.Vision.Conf          = p.Conf;
        _cfg.Vision.Strength      = p.Strength;
        _cfg.Vision.MaxStep       = p.MaxStep;
        _cfg.Vision.Smooth        = p.Smooth;
        _cfg.Vision.Prediction    = p.Prediction;
        _cfg.Vision.AimYOffset    = p.AimYOffset;
        _cfg.Vision.ConfirmFrames = (int)p.ConfirmFrames;
        _cfg.Vision.StopDist      = p.StopDist;
        _cfg.Triggerbot.Tolerance = p.TbTolerance;
        _cfg.Triggerbot.DelayMin  = p.TbDelayMin;
        _cfg.Triggerbot.DelayMax  = p.TbDelayMax;
        _cfg.Triggerbot.TargetSwitchDelayMs  = p.TbTargetSwitchDelayMs;
        _cfg.Triggerbot.TargetSwitchRadiusPx = p.TbTargetSwitchRadiusPx;
        _cfg.Triggerbot.AimOnly   = p.TbAimOnly;
        _cfg.Triggerbot.Enabled   = p.TbEnabled;

        if (_vision != null)
        {
            _vision.SetFov((int)p.FovRadius);
            _vision.SetConf((float)p.Conf);
            _vision.AimYOffsetPercent = (float)p.AimYOffset;
            _vision.PredictionStr = (float)p.Prediction;
            _vision.ConfirmFrames = (int)p.ConfirmFrames;
            _vision.StopDist      = (float)p.StopDist;
        }
        // ★ FIX: перерисовываем оверлей — обновляем FOV-круг и capture-квадрат
        _overlay?.UpdateFov();

        _mouse.Strength    = (float)p.Strength;
        _mouse.MaxStep     = (float)p.MaxStep;
        _mouse.Smooth      = (float)p.Smooth;
        _mouse.TbEnabled   = p.TbEnabled;
        _mouse.TbTolerance = (float)p.TbTolerance;
        _mouse.TbDelayMin  = (float)p.TbDelayMin / 1000f;
        _mouse.TbDelayMax  = (float)p.TbDelayMax / 1000f;
        _mouse.TbTargetSwitchDelayMs  = (float)p.TbTargetSwitchDelayMs;
        _mouse.TbTargetSwitchRadiusPx = (float)p.TbTargetSwitchRadiusPx;
        _mouse.TbAimOnly   = p.TbAimOnly;

        if (p.RangefinderEnabled && p.Rangefinder.Count > 0)
            _mouse.RfSetTable(p.Rangefinder, true, true);
        else
            _mouse.RfSetTable([], false);
        _mouse.RfBaseYOffset = 0;

        RefreshPresetSidebar();
        // Точечно уведомляем биндинги — без DataContext=null который стреляет ValueChanged с 0
        NotifyPreset();
        Console.WriteLine($"[preset] {p.Name}");
    }

    private void DeactivatePreset()
    {
        ActivePresetIndex = -1;
        _mouse.RfSetTable(_cfg.Rangefinder.Table, _cfg.Rangefinder.Enabled);
        RefreshPresetSidebar();
        NotifyPreset();
    }

    // Уведомляем WPF о смене VisionCfg/TbCfg под защитой _initUi,
    // чтобы ValueChanged на слайдерах не перезаписал свежие данные нулями
    private void NotifyPreset()
    {
        _initUi = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisionCfg)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TbCfg)));
        _initUi = false;
    }

    // ── Actions ───────────────────────────────────────────────────────────────
    private void ToggleGui()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    private void NextDeadZoneGroup()
    {
        if (_cfg.DeadZoneGroups.Count == 0) return;
        // Циклический переход по группам: 0 → 1 → 2 → ... → 0
        _cfg.ActiveDeadZoneGroup = (_cfg.ActiveDeadZoneGroup + 1) % _cfg.DeadZoneGroups.Count;
        ApplyActiveDeadZoneGroup();
        Console.WriteLine($"[dz] Группа → {_cfg.CurrentGroupName} ({_cfg.ActiveDeadZoneGroup})");
    }

    private void ApplyActiveDeadZoneGroup()
    {
        int gi = _cfg.ActiveDeadZoneGroup;
        if (_vision != null)
            _vision.DeadZones = _cfg.CurrentZones.Select(d => new DeadZone(d)).ToList();
        _overlay?.UpdateFov();
        // Синхронизируем UI панели зон
        Dispatcher.Invoke(() =>
        {
            DeadZonesControl.ActiveGroupIndex = gi;
        });
        _dirty = true;
    }

    private void SyncBinds()
    {
        var b = _cfg.Binds;
        _mouse.BindAim          = b.Aim;
        _mouse.BindSwitchTarget = b.SwitchTarget;
        _mouse.BindToggle       = b.Toggle;
        _mouse.BindOverlay      = b.Overlay;
        _mouse.BindHideGui      = b.HideGui;
        _mouse.BindDeadZone     = b.DeadZone;
        _mouse.BindTriggerbot   = b.Triggerbot;
        _mouse.BindClassToggle  = b.ClassToggle;
        _mouse.BindRangefinder  = b.Rangefinder;
        _mouse.BindExit         = b.Exit;
    }

    private void SyncPresetBinds()
    {
        _mouse.PresetBinds = _presets
            .Select((p, i) => (key: p.Bind?.Trim().ToLower() ?? "", idx: i))
            .Where(x => !string.IsNullOrEmpty(x.key))
            .ToDictionary(x => x.key, x => x.idx);
    }

    // ── Save / Exit ───────────────────────────────────────────────────────────
    private void DoSave()
    {
        // ★ Если активен пресет — сохраняем текущие значения главных слайдеров обратно в него
        if (_activePreset >= 0 && _activePreset < _presets.Count)
        {
            var p = _presets[_activePreset];
            p.FovRadius     = _cfg.Vision.FovRadius;
            p.Conf          = _cfg.Vision.Conf;
            p.Strength      = _cfg.Vision.Strength;
            p.MaxStep       = _cfg.Vision.MaxStep;
            p.Smooth        = _cfg.Vision.Smooth;
            p.Prediction    = _cfg.Vision.Prediction;
            p.AimYOffset    = _cfg.Vision.AimYOffset;
            p.ConfirmFrames = _cfg.Vision.ConfirmFrames;
            p.StopDist      = _cfg.Vision.StopDist;
            p.TbTolerance   = _cfg.Triggerbot.Tolerance;
            p.TbDelayMin    = _cfg.Triggerbot.DelayMin;
            p.TbDelayMax    = _cfg.Triggerbot.DelayMax;
            p.TbTargetSwitchDelayMs  = _cfg.Triggerbot.TargetSwitchDelayMs;
            p.TbTargetSwitchRadiusPx = _cfg.Triggerbot.TargetSwitchRadiusPx;
            p.TbAimOnly     = _cfg.Triggerbot.AimOnly;
            p.TbEnabled     = _cfg.Triggerbot.Enabled;
            Console.WriteLine($"[save] Пресет '{p.Name}' обновлён из текущих настроек");
        }

        _cfg.ActivePreset        = _activePreset;
        _cfg.ActiveDeadZoneGroup = DeadZonesControl.ActiveGroupIndex;
        var presetsCopy          = _presets.ToList();
        _cfg.Presets             = presetsCopy;
        ConfigManager.Save(_cfg);
        ConfigManager.SavePresets(presetsCopy);
        foreach (var p in presetsCopy)
            Console.WriteLine($"[save] {p.Name}: strength={p.Strength:F3} maxStep={p.MaxStep:F1} fov={p.FovRadius:F0}");
        Console.WriteLine($"[save] OK — preset={_activePreset}, dzGroup={_cfg.ActiveDeadZoneGroup}, total={presetsCopy.Count}");

        // Обновляем карточки пресетов чтобы слайдеры показали актуальные значения.
        // Отписываемся на время Rebuild — иначе ValueChanged слайдеров ставит _dirty = true
        // и через 2 секунды снова запускает DoSave (бесконечный цикл).
        PresetsControl.PresetChanged -= OnPresetCardChanged;
        PresetsControl.Presets     = null;
        PresetsControl.Presets     = _presets;
        PresetsControl.ActiveIndex = _activePreset;
        PresetsControl.PresetChanged += OnPresetCardChanged;
    }

    private void DoExit()
    {
        DoSave();
        _vision?.Stop();
        _mouse.Dispose();
        ScreenCaptureFactory.Dispose(); // освобождаем DXGI/GDI ресурсы
        _indicator?.Close();
        _overlay?.Close();
        Application.Current.Shutdown();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e) => DoExit();

    // ── UI Event Handlers ─────────────────────────────────────────────────────
    private void OnIndicatorEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_initUi) return;
        bool enabled = (sender as CheckBox)?.IsChecked
            ?? Ui<CheckBox>("IndEnabledCheck")?.IsChecked
            ?? _cfg.IndicatorEnabled;
        _cfg.IndicatorEnabled = enabled;
        if (_indicator != null)
        {
            if (enabled) _indicator.Show();
            else         _indicator.Hide();
        }
        _dirty = true;
    }

    private void OnIndicatorAlphaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initUi) return;
        _cfg.IndicatorAlpha = e.NewValue;
        _indicator?.SetAlpha((float)e.NewValue);
        _dirty = true;
    }

    private void OnIndicatorPosKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Return) ApplyIndicatorPos();
    }

    private void OnIndicatorPosChanged(object sender, RoutedEventArgs e) => ApplyIndicatorPos();

    private void ApplyIndicatorPos()
    {
        var indXBox = Ui<TextBox>("IndXBox");
        var indYBox = Ui<TextBox>("IndYBox");
        if (!int.TryParse(indXBox?.Text, out int x)) x = _cfg.IndicatorX >= 0 ? _cfg.IndicatorX : 20;
        if (!int.TryParse(indYBox?.Text, out int y)) y = _cfg.IndicatorY >= 0 ? _cfg.IndicatorY : 20;
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        _cfg.IndicatorX = x;
        _cfg.IndicatorY = y;
        if (indXBox != null) indXBox.Text = x.ToString();
        if (indYBox != null) indYBox.Text = y.ToString();
        _indicator?.SetPosition(x, y);
        _dirty = true;
    }

    private void UpdateClassUi()
    {
        string text = _vision == null ? "Класс: —" : $"Класс: {_vision.ActiveClassName}";

        if (Ui<TextBlock>("ClassModeText") is { } tb)
            tb.Text = text;
        if (Ui<Label>("ClassModeLabel") is { } lbl)
            lbl.Content = text;
        if (Ui<Button>("ClassToggleButton") is { } btn)
            btn.Content = _vision == null ? "Класс: —" : $"Класс: {_vision.ActiveClassName}  ↔";
    }

    private void OnClassToggleClick(object sender, RoutedEventArgs e)
    {
        if (_vision == null) return;
        int cls = _vision.ToggleDetectionClass();
        _cfg.Vision.DetectionClass = cls;
        UpdateClassUi();
        _dirty = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DoSave();
        _dirty = false;
        // ★ FIX: визуальная обратная связь — кнопок "Сохранить" может быть несколько,
        // используем sender чтобы не зависеть от x:Name
        if (sender is not Button btn) return;
        btn.Content    = "✓ СОХРАНЕНО";
        btn.Background = new SolidColorBrush(Color.FromRgb(10, 74, 10));
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) =>
        {
            btn.Content    = "СОХРАНИТЬ";
            btn.Background = new SolidColorBrush(Color.FromRgb(180, 30, 30));
            t.Stop();
        };
        t.Start();
    }

    private void OnFovChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.FovRadius = (int)e.NewValue; _vision?.SetFov((int)e.NewValue); _overlay?.UpdateFov(); _dirty = true; }

    private void OnConfChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.Conf = e.NewValue; _vision?.SetConf((float)e.NewValue); _dirty = true; }

    private void OnStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.Strength = e.NewValue; _mouse.Strength = (float)e.NewValue; _dirty = true; }

    private void OnMaxStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.MaxStep = e.NewValue; _mouse.MaxStep = (float)e.NewValue; _dirty = true; }

    private void OnSmoothChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.Smooth = e.NewValue; _mouse.Smooth = (float)e.NewValue; _dirty = true; }

    private void OnAimYOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // AimYOffset is stored in the old config field for compatibility,
        // but now means percent of current target bbox height, not absolute pixels.
        if (_initUi) return;
        _cfg.Vision.AimYOffset = e.NewValue;
        if (_vision != null) _vision.AimYOffsetPercent = (float)e.NewValue;
        _dirty = true;
    }

    private void OnPredictionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.Prediction = e.NewValue; if (_vision != null) _vision.PredictionStr = (float)e.NewValue; _dirty = true; }

    private void OnStopDistChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.StopDist = e.NewValue; if (_vision != null) _vision.StopDist = (float)e.NewValue; _dirty = true; }

    private void OnConfirmFramesChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Vision.ConfirmFrames = (int)e.NewValue; if (_vision != null) _vision.ConfirmFrames = (int)e.NewValue; _dirty = true; }

    private void OnTbToleranceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Triggerbot.Tolerance = e.NewValue; _mouse.TbTolerance = (float)e.NewValue; _dirty = true; }

    private void OnTbDelayMinChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Triggerbot.DelayMin = e.NewValue; _mouse.TbDelayMin = (float)e.NewValue / 1000f; _dirty = true; }

    private void OnTbDelayMaxChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Triggerbot.DelayMax = e.NewValue; _mouse.TbDelayMax = (float)e.NewValue / 1000f; _dirty = true; }

    private void OnTbTargetSwitchDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Triggerbot.TargetSwitchDelayMs = e.NewValue; _mouse.TbTargetSwitchDelayMs = (float)e.NewValue; _dirty = true; }

    private void OnTbTargetSwitchRadiusChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_initUi) return; _cfg.Triggerbot.TargetSwitchRadiusPx = e.NewValue; _mouse.TbTargetSwitchRadiusPx = (float)e.NewValue; _dirty = true; }

    private void OnTbEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_initUi) return;
        _cfg.Triggerbot.Enabled = (sender as CheckBox)?.IsChecked
            ?? Ui<CheckBox>("TbEnabledCheck")?.IsChecked
            ?? _cfg.Triggerbot.Enabled;
        _mouse.TbEnabled = _cfg.Triggerbot.Enabled;
        _dirty = true;
    }

    private void OnVisionEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_initUi) return;
        _cfg.Vision.Enabled = (sender as CheckBox)?.IsChecked
            ?? Ui<CheckBox>("VisionEnabledCheck")?.IsChecked
            ?? _cfg.Vision.Enabled;
        _dirty = true;
    }

    private void OnShowFovChanged(object sender, RoutedEventArgs e)
    {
        if (_initUi) return;
        _cfg.Vision.ShowFov = (sender as CheckBox)?.IsChecked
            ?? Ui<CheckBox>("ShowFovCheck")?.IsChecked
            ?? _cfg.Vision.ShowFov;
        if (_cfg.Vision.ShowFov) _overlay?.Show(); else _overlay?.Hide();
        _dirty = true;
    }

    private void OnDeadZoneEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_initUi) return;
        _cfg.Vision.DeadZone = (sender as CheckBox)?.IsChecked
            ?? Ui<CheckBox>("DeadZoneCheck")?.IsChecked
            ?? _cfg.Vision.DeadZone;
        if (_vision != null) _vision.DeadZoneEnabled = _cfg.Vision.DeadZone;
        _overlay?.UpdateFov();
        _dirty = true;
    }

    private void OnCaptureModeChanged(object sender, RoutedEventArgs e)
    {
        if (_initUi) return;

        bool tiled;
        if (sender is RadioButton rb)
        {
            // Обрабатываем только Checked, а не Unchecked другого radio.
            if (rb.IsChecked != true) return;

            string n = rb.Name ?? string.Empty;
            string tag = rb.Tag?.ToString() ?? string.Empty;
            string content = rb.Content?.ToString() ?? string.Empty;
            string key = (n + " " + tag + " " + content).ToLowerInvariant();

            if (key.Contains("tile") || key.Contains("тайл"))
                tiled = true;
            else if (key.Contains("squish") || key.Contains("compress") || key.Contains("resize")
                  || key.Contains("normal") || key.Contains("single")
                  || key.Contains("сжат") || key.Contains("обыч"))
                tiled = false;
            else
                tiled = Ui<RadioButton>("ModeTiled")?.IsChecked == true;
        }
        else
        {
            tiled = Ui<RadioButton>("ModeTiled")?.IsChecked == true;
        }

        _cfg.Vision.UseTiled = tiled;
        if (_vision != null) _vision.UseTiled = tiled;
        SyncCaptureModeUi(tiled);
        UpdateCaptureSizeHint(_cfg.Vision.CaptureSize, tiled);
        _dirty = true;
    }

    private void OnCaptureSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initUi) return; // не трогаем конфиг во время сброса DataContext
        int val = Math.Max(64, (int)e.NewValue);
        _cfg.Vision.CaptureSize = val;
        _vision?.SetCaptureSize(val);
        UpdateCaptureSizeHint(val, _cfg.Vision.UseTiled);
        _dirty = true;
    }

    private void UpdateCaptureSizeHint(int size, bool tiled)
    {
        var captureSizeHint = Ui<TextBlock>("CaptureSizeHint");
        if (captureSizeHint == null) return;
        int modelSize = _vision?.ModelInputSize > 0 ? _vision.ModelInputSize : 416;
        if (tiled)
        {
            int tiles = (int)Math.Round((double)size / modelSize);
            tiles = Math.Max(1, tiles);
            string grid = tiles switch { 1 => "1×1", 2 => "2×2", 3 => "3×3", 4 => "4×4", _ => $"{tiles}×{tiles}" };
            string perf = tiles switch { 1 => "лёгкая нагрузка", 2 => "умеренная нагрузка", 3 => "высокая нагрузка", _ => "очень высокая нагрузка" };
            int recommended = modelSize * 2; // 2×2 — баланс качества и скорости
            string tip = size == recommended ? " ✓ рекомендуется" : size < recommended ? " (лучше поставить 832)" : "";
            captureSizeHint.Text = $"💡 Тайлы: {grid} ({tiles*tiles} инференсов/кадр) — {perf}{tip}";
            captureSizeHint.Foreground = tiles <= 2
                ? new SolidColorBrush(Color.FromRgb(0, 180, 100))
                : new SolidColorBrush(Color.FromRgb(255, 140, 0));
        }
        else
        {
            string tip = size switch
            {
                <= 320 => "быстро, хуже видит мелких/дальних",
                <= 480 => "✓ оптимальный баланс",
                <= 640 => "✓ хорошее качество",
                _      => "медленно, избыточно для сжатия"
            };
            captureSizeHint.Text = $"💡 Сжатие: {tip}";
            captureSizeHint.Foreground = size is > 320 and <= 640
                ? new SolidColorBrush(Color.FromRgb(0, 180, 100))
                : new SolidColorBrush(Color.FromRgb(150, 150, 150));
        }
    }
    private void OnPresetActivate(object sender, RoutedEventArgs e)
    { if (sender is Button btn && btn.Tag is int idx) ApplyPreset(idx); }

    private void OnDeadZonesChanged(object sender, EventArgs e)
    {
        if (_vision != null)
            _vision.DeadZones = _cfg.CurrentZones.Select(d => new DeadZone(d)).ToList();
        _overlay?.UpdateFov();
        _dirty = true;
    }

    private void OnDeadZoneGroupSwitched(object sender, int gi)
    {
        _cfg.ActiveDeadZoneGroup = gi;
        ApplyActiveDeadZoneGroup();
    }

    private void OnPresetCardChanged(object? s, EventArgs e) => _dirty = true;

    private void OnBindChanged(object sender, EventArgs e)
    { SyncBinds(); SyncPresetBinds(); _dirty = true; }
}
