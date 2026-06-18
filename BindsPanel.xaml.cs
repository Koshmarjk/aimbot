// UI/BindsPanel.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HachBobAI.Config;

namespace HachBobAI.UI;

public partial class BindsPanel : UserControl
{
    public static readonly DependencyProperty BindsProperty =
        DependencyProperty.Register(nameof(Binds), typeof(BindsConfig), typeof(BindsPanel),
            new PropertyMetadata(null, (d, _) => ((BindsPanel)d).Rebuild()));

    public BindsConfig? Binds { get => (BindsConfig?)GetValue(BindsProperty); set => SetValue(BindsProperty, value); }

    public event EventHandler? BindChanged;

    private Button? _listening;
    private Action<string>? _applyBind;

    private static readonly Dictionary<string, string> KeyLabels = new()
    {
        ["insert"]="INS",["home"]="HOME",["end"]="END",["delete"]="DEL",
        ["f1"]="F1",["f2"]="F2",["f3"]="F3",["f4"]="F4",["f5"]="F5",
        ["f6"]="F6",["f7"]="F7",["f8"]="F8",["f9"]="F9",["f10"]="F10",
        ["f11"]="F11",["f12"]="F12",
        ["x1"]="MB4",["x2"]="MB5",["lmb"]="LMB",["rmb"]="RMB",
        ["\\"]="\\",["space"]="SPACE",["v"]="V",["r"]="R",
    };

    private static string Display(string key) =>
        KeyLabels.TryGetValue(key.ToLower(), out var lbl) ? lbl : key.ToUpper();

    public BindsPanel() { InitializeComponent(); }

    private void Rebuild()
    {
        PART_Rows.Children.Clear();
        var b = Binds;
        if (b == null) return;

        var rows = new (string label, Func<string> get, Action<string> set)[]
        {
            ("Аим (удержание)",    () => b.Aim,          v => b.Aim          = v),
            ("Смена цели",         () => b.SwitchTarget,  v => b.SwitchTarget = v),
            ("Вкл / Выкл",         () => b.Toggle,        v => b.Toggle       = v),
            ("Overlay",            () => b.Overlay,        v => b.Overlay      = v),
            ("Скрыть GUI",         () => b.HideGui,        v => b.HideGui      = v),
            ("Мёртвые зоны",       () => b.DeadZone,       v => b.DeadZone     = v),
            ("Тригербот вкл/выкл", () => b.Triggerbot,     v => b.Triggerbot   = v),
            ("Дальномер вкл/выкл", () => b.Rangefinder,    v => b.Rangefinder  = v),
            ("Выход",              () => b.Exit,            v => b.Exit         = v),
        };

        foreach (var (label, getter, setter) in rows)
        {
            var row = new Grid { Margin = new Thickness(0,3,0,3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = label, Foreground = new SolidColorBrush(Color.FromRgb(170,170,170)),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);

            var btn = new Button
            {
                Content = Display(getter()), Width=110, Height=28,
                FontFamily = new FontFamily("Consolas"), FontSize=12, FontWeight=FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(26,26,58)),
                Foreground = Brushes.LightGray,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58,58,122)), BorderThickness=new Thickness(1),
            };

            var captureSet = setter;
            var captureGet = getter;
            var captureBtn = btn;
            btn.Click += (_, _) =>
            {
                if (_listening == captureBtn)
                {
                    // Cancel listen
                    StopListen(captureBtn, captureGet());
                    return;
                }
                if (_listening != null) StopListen(_listening, "");
                StartListen(captureBtn, captureSet);
            };

            Grid.SetColumn(btn, 1);
            row.Children.Add(lbl);
            row.Children.Add(btn);
            PART_Rows.Children.Add(row);
        }
    }

    private void StartListen(Button btn, Action<string> setter)
    {
        _listening = btn;
        _applyBind = key =>
        {
            setter(key);
            btn.Content = Display(key);
            btn.Background = new SolidColorBrush(Color.FromRgb(10,42,10));
            btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0,204,102));
            BindChanged?.Invoke(this, EventArgs.Empty);
            StopListen(btn, key);
        };
        btn.Content = "...";
        btn.Background = new SolidColorBrush(Color.FromRgb(58,26,26));
        btn.BorderBrush = new SolidColorBrush(Color.FromRgb(255,75,75));
        Window.GetWindow(this)?.AddHandler(KeyDownEvent,   (KeyEventHandler)OnCaptureKey,    true);
        Window.GetWindow(this)?.AddHandler(MouseDownEvent, (MouseButtonEventHandler)OnCaptureMouse, true);
    }

    private void StopListen(Button btn, string currentKey)
    {
        _listening = null;
        _applyBind = null;
        btn.Content = Display(currentKey);
        btn.Background = new SolidColorBrush(Color.FromRgb(26,26,58));
        btn.BorderBrush = new SolidColorBrush(Color.FromRgb(58,58,122));
        Window.GetWindow(this)?.RemoveHandler(KeyDownEvent,   (KeyEventHandler)OnCaptureKey);
        Window.GetWindow(this)?.RemoveHandler(MouseDownEvent, (MouseButtonEventHandler)OnCaptureMouse);
    }

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        string key = e.Key switch
        {
            Key.Insert    => "insert",
            Key.Home      => "home",
            Key.Delete    => "delete",
            Key.End       => "end",
            Key.Prior     => "page_up",
            Key.Next      => "page_down",
            Key.F1 => "f1", Key.F2 => "f2", Key.F3 => "f3", Key.F4 => "f4",
            Key.F5 => "f5", Key.F6 => "f6", Key.F7 => "f7", Key.F8 => "f8",
            Key.F9 => "f9", Key.F10 => "f10", Key.F11 => "f11", Key.F12 => "f12",
            Key.OemBackslash => "\\",
            Key.Space        => "space",
            _ => e.Key.ToString().ToLower()
        };
        _applyBind?.Invoke(key);
    }

    private void OnCaptureMouse(object sender, MouseButtonEventArgs e)
    {
        string btn = e.ChangedButton switch
        {
            MouseButton.Left   => "lmb",
            MouseButton.Right  => "rmb",
            MouseButton.Middle => "mmb",
            MouseButton.XButton1 => "x1",
            MouseButton.XButton2 => "x2",
            _ => ""
        };
        if (!string.IsNullOrEmpty(btn)) _applyBind?.Invoke(btn);
    }
}
