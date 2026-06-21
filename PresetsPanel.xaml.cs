// UI/PresetsPanel.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HachBobAI.Config;

namespace HachBobAI.UI;

public partial class PresetsPanel : UserControl
{
    public static readonly DependencyProperty PresetsProperty =
        DependencyProperty.Register(nameof(Presets), typeof(List<PresetConfig>), typeof(PresetsPanel),
            new PropertyMetadata(null, (d, _) => ((PresetsPanel)d).Rebuild()));

    public static readonly DependencyProperty ActiveIndexProperty =
        DependencyProperty.Register(nameof(ActiveIndex), typeof(int), typeof(PresetsPanel),
            new PropertyMetadata(-1, (d, _) => ((PresetsPanel)d).Rebuild()));

    public List<PresetConfig>? Presets    { get => (List<PresetConfig>?)GetValue(PresetsProperty); set => SetValue(PresetsProperty, value); }
    public int                 ActiveIndex{ get => (int)GetValue(ActiveIndexProperty);              set => SetValue(ActiveIndexProperty, value); }

    public event RoutedEventHandler? OnApply;
    public event EventHandler?       PresetChanged; // ← новое: слайдер/поле изменилось

    private static readonly string[] Colors =
        ["#3A7BD5","#00CC66","#FF8C00","#FF4B4B","#AA44FF","#00AAFF","#FFD700","#FF44AA"];

    public PresetsPanel() { InitializeComponent(); }

    private bool _building; // блокирует запись в PresetConfig во время построения карточек

    private void Rebuild()
    {
        PART_Cards.Children.Clear();
        var presets = Presets;
        if (presets == null) return;

        for (int i = 0; i < presets.Count; i++)
            PART_Cards.Children.Add(BuildCard(i, presets[i]));
    }

    private UIElement BuildCard(int idx, PresetConfig p)
    {
        bool isActive = idx == ActiveIndex;
        string hexCol = Colors[idx % Colors.Length];
        var col = (Color)ColorConverter.ConvertFromString(hexCol);
        var colBrush = new SolidColorBrush(col);
        var borderCol = isActive ? Color.FromRgb(0,255,136) : col;

        // --- Card border ---
        var card = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(13,13,32)),
            BorderBrush     = new SolidColorBrush(borderCol),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius    = new CornerRadius(6),
            Margin          = new Thickness(2,4,2,4),
            Padding         = new Thickness(10,8,10,8),
        };

        var stack = new StackPanel();

        // --- Header row ---
        var head = new WrapPanel { Margin = new Thickness(0,0,0,8) };

        var dot = new TextBlock { Text="■", Foreground=colBrush, FontSize=14, Margin=new Thickness(0,0,6,0), VerticalAlignment=VerticalAlignment.Center };
        head.Children.Add(dot);

        var nameBox = new TextBox
        {
            Text = p.Name, Width=100, Background=new SolidColorBrush(Color.FromRgb(10,10,24)),
            Foreground=colBrush, BorderBrush=colBrush, FontSize=13, FontWeight=FontWeights.Bold,
            Margin=new Thickness(0,0,8,0)
        };
        nameBox.TextChanged += (_, _) => p.Name = nameBox.Text;
        head.Children.Add(nameBox);

        var bindBtn = new Button
        {
            Content = string.IsNullOrEmpty(p.Bind) ? "—" : p.Bind.ToUpper(),
            Width=72, Height=24, FontFamily=new FontFamily("Consolas"), FontSize=10, FontWeight=FontWeights.Bold,
            Background=new SolidColorBrush(Color.FromRgb(26,26,58)), Foreground=Brushes.LightGray,
            BorderBrush=new SolidColorBrush(Color.FromRgb(58,58,122)), BorderThickness=new Thickness(1),
            Margin=new Thickness(0,0,6,0), ToolTip="Нажми, затем нажми клавишу/кнопку"
        };
        bindBtn.Click += (_, _) => StartBindCapture(idx, bindBtn);
        head.Children.Add(bindBtn);

        var applyBtn = new Button
        {
            Content=isActive ? "● АКТИВЕН" : "▶ ПРИМЕНИТЬ",
            Width=110, Height=24, FontSize=11, FontWeight=FontWeights.Bold,
            Background=new SolidColorBrush(isActive ? Color.FromRgb(10,74,10) : Color.FromRgb(26,58,26)),
            Foreground=new SolidColorBrush(Color.FromRgb(0,204,102)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(0,204,102)), BorderThickness=new Thickness(1),
            Margin=new Thickness(0,0,4,0), Tag=idx
        };
        applyBtn.Click += (s, e) => OnApply?.Invoke(s, e);
        head.Children.Add(applyBtn);

        var delBtn = new Button
        {
            Content="✕", Width=26, Height=24, FontSize=12,
            Background=new SolidColorBrush(Color.FromRgb(42,0,0)),
            Foreground=new SolidColorBrush(Color.FromRgb(255,85,85)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(85,0,0)), BorderThickness=new Thickness(1),
        };
        delBtn.Click += (_, _) => { Presets?.RemoveAt(idx); Rebuild(); };
        head.Children.Add(delBtn);

        stack.Children.Add(head);

        // --- Parameter grid (2 columns) ---
        var paramGrid = new Grid { Margin=new Thickness(0,0,0,6) };
        paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1, GridUnitType.Star) });
        paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1, GridUnitType.Star) });

        var fields = new (string key, string label, double min, double max, string fmt)[]
        {
            ("FovRadius",     "FOV радиус",          80,   600,  "F0"),
            ("Conf",          "Уверенность",          0.10, 0.90, "F2"),
            ("Strength",      "Сила наведения",       0.03, 0.50, "F3"),
            ("MaxStep",       "Макс. шаг",            2.0,  60.0, "F1"),
            ("Smooth",        "Плавность",            0.0,  1.0,  "F2"),  // min=0, значение 0.0 допустимо
            ("Prediction",    "Предикция",            0.0,  1.5,  "F2"),
            ("AimYOffset",    "Y-смещение",          -100,  100,  "F0"),
            ("ConfirmFrames", "Подтверждение кадров", 0,    15,   "F0"),
            ("StopDist",      "Стоп-зона (px)",       0.5,  20.0, "F1"),
            ("TbTolerance",   "Радиус триггера",      1.0,  200.0,"F0"),
            ("TbDelayMin",    "Задержка мин (мс)",    0.0,  500.0,"F0"),
            ("TbDelayMax",    "Задержка макс (мс)",   0.0,  500.0,"F0"),
            ("TbTargetSwitchDelayMs",  "Задержка смены цели", 0.0, 500.0,"F0"),
            ("TbTargetSwitchRadiusPx", "Радиус смены цели",   1.0, 300.0,"F0"),
        };

        int rows = (int)Math.Ceiling(fields.Length / 2.0);
        for (int r = 0; r < rows; r++)
            paramGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _building = true;
        for (int fi = 0; fi < fields.Length; fi++)
        {
            var (key, label, fmin, fmax, fmt) = fields[fi];
            var prop = typeof(PresetConfig).GetProperty(key)!;
            double curVal = Convert.ToDouble(prop.GetValue(p));

            var cell = new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(4,2,4,2) };

            var lbl = new TextBlock { Text=label, Foreground=new SolidColorBrush(Color.FromRgb(119,119,119)),
                FontSize=10, Width=110, VerticalAlignment=VerticalAlignment.Center };
            cell.Children.Add(lbl);

            var sl = new Slider { Minimum=fmin, Maximum=fmax, Value=Math.Clamp(curVal, fmin, fmax),
                Width=80, VerticalAlignment=VerticalAlignment.Center,
                Foreground=colBrush, Margin=new Thickness(4,0,4,0) };

            var entry = new TextBox { Text=curVal.ToString(fmt), Width=52, Height=22,
                Background=new SolidColorBrush(Color.FromRgb(10,10,24)),
                Foreground=colBrush, BorderBrush=new SolidColorBrush(Color.FromRgb(42,42,85)),
                FontFamily=new FontFamily("Consolas"), FontSize=10, TextAlignment=TextAlignment.Center };

            bool lk = false;
            sl.ValueChanged += (_, e) =>
            {
                if (lk || _building) return; lk = true;
                double v = e.NewValue;
                prop.SetValue(p, Convert.ChangeType(v, prop.PropertyType));
                entry.Text = v.ToString(fmt);
                lk = false;
                PresetChanged?.Invoke(this, EventArgs.Empty);
            };
            void applyEntry(object? _, RoutedEventArgs __)
            {
                if (lk || !double.TryParse(entry.Text.Replace("+","").Trim(), out double v)) return;
                v = Math.Clamp(v, fmin, fmax);
                lk = true;
                prop.SetValue(p, Convert.ChangeType(v, prop.PropertyType));
                sl.Value = v; entry.Text = v.ToString(fmt);
                lk = false;
                PresetChanged?.Invoke(this, EventArgs.Empty);
            }
            entry.LostFocus += applyEntry;
            entry.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Return) applyEntry(s, e); };

            cell.Children.Add(sl);
            cell.Children.Add(entry);

            Grid.SetRow(cell, fi / 2);
            Grid.SetColumn(cell, fi % 2);
            paramGrid.Children.Add(cell);
        }
        _building = false; // разблокируем ValueChanged после построения всех слайдеров

        stack.Children.Add(paramGrid);

        // --- TB checkboxes row ---
        var tbRow = new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,2,0,0) };
        var tbEnCb = new CheckBox { Content="🎯 TB включён", IsChecked=p.TbEnabled,
            Foreground=new SolidColorBrush(Color.FromRgb(0,204,102)), FontSize=10, Margin=new Thickness(0,0,16,0) };
        tbEnCb.Click += (_, _) => p.TbEnabled = tbEnCb.IsChecked == true;
        var tbAoCb = new CheckBox { Content="только при аиме", IsChecked=p.TbAimOnly,
            Foreground=new SolidColorBrush(Color.FromRgb(0,153,204)), FontSize=10 };
        tbAoCb.Click += (_, _) => p.TbAimOnly = tbAoCb.IsChecked == true;
        tbRow.Children.Add(tbEnCb);
        tbRow.Children.Add(tbAoCb);
        stack.Children.Add(tbRow);

        card.Child = stack;
        return card;
    }

    // --- Bind capture ---
    private int    _captureIdx = -1;
    private Button? _captureBtn;

    private void StartBindCapture(int idx, Button btn)
    {
        if (_captureIdx >= 0)
            Window.GetWindow(this)?.RemoveHandler(KeyDownEvent, (KeyEventHandler)OnCaptureKey);
        _captureIdx = idx;
        _captureBtn = btn;
        btn.Content = "...";
        btn.Background = new SolidColorBrush(Color.FromRgb(58,26,26));
        Window.GetWindow(this)?.AddHandler(KeyDownEvent, (KeyEventHandler)OnCaptureKey, true);
    }

    private void OnCaptureKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Window.GetWindow(this)?.RemoveHandler(KeyDownEvent, (KeyEventHandler)OnCaptureKey);
        if (_captureIdx < 0 || Presets == null || _captureIdx >= Presets.Count) return;
        string key = e.Key.ToString().ToLower().Replace("f1", "f1"); // already correct for F-keys
        // Normalize
        key = e.Key switch
        {
            System.Windows.Input.Key.Insert => "insert",
            System.Windows.Input.Key.Home   => "home",
            System.Windows.Input.Key.Delete => "delete",
            System.Windows.Input.Key.End    => "end",
            _ => e.Key.ToString().ToLower()
        };
        Presets[_captureIdx].Bind = key;
        if (_captureBtn != null)
        {
            _captureBtn.Content    = key.ToUpper();
            _captureBtn.Background = new SolidColorBrush(Color.FromRgb(10,42,10));
        }
        _captureIdx = -1;
        _captureBtn = null;
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        Presets?.Add(new PresetConfig { Name = $"Пресет {(Presets.Count+1)}", Bind = "" });
        Rebuild();
    }
}
