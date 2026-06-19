// UI/Windows.cs — Overlay + Indicator windows
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HachBobAI.Vision;

namespace HachBobAI;

// ═══════════════════════════════════════════════════════════════════════════════
// Overlay — прозрачное окно поверх игры (FOV, боксы, статус)
// ═══════════════════════════════════════════════════════════════════════════════
public sealed class OverlayWindow : Window
{
    private readonly VisionEngine _engine;
    private readonly Canvas       _canvas;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _visible;

    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int idx, int style);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int idx);

    public OverlayWindow(VisionEngine engine)
    {
        _engine = engine;

        double sw = SystemParameters.PrimaryScreenWidth;
        double sh = SystemParameters.PrimaryScreenHeight;

        WindowStyle        = WindowStyle.None;
        ResizeMode         = ResizeMode.NoResize;
        Width = sw; Height = sh;
        Left  = 0;  Top    = 0;
        Topmost            = true;
        IsHitTestVisible   = false;
        AllowsTransparency = true;
        Background         = Brushes.Transparent;
        ShowInTaskbar      = false;

        _canvas = new Canvas { Width = sw, Height = sh };
        Content = _canvas;

        Loaded += (_, _) => MakeClickThrough();
        BuildFovShape();

        _timer.Tick    += (_, _) => Redraw();
        WindowState     = WindowState.Maximized;
        Visibility      = Visibility.Hidden;
    }

    private void MakeClickThrough()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            const int GWL_EXSTYLE   = -20;
            const int WS_EX_TRANS   = 0x00000020;
            const int WS_EX_LAYERED = 0x00080000;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANS | WS_EX_LAYERED);
        }
        catch { }
    }

    private void BuildFovShape()
    {
        _canvas.Children.Clear();
        float cx  = _engine.ScreenCx;
        float cy  = _engine.ScreenCy;
        float r   = _engine.FovRadius;
        int   cap = _engine._capSize / 2;

        // Capture region
        var capRect = new Rectangle
        {
            Width = _engine._capSize, Height = _engine._capSize,
            Stroke = new SolidColorBrush(Color.FromArgb(128, 58, 123, 213)),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 6 },
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(capRect, cx - cap);
        Canvas.SetTop (capRect, cy - cap);
        _canvas.Children.Add(capRect);

        // FOV circle
        var fovCircle = new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 75, 75)),
            StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 8, 4 },
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(fovCircle, cx - r);
        Canvas.SetTop (fovCircle, cy - r);
        _canvas.Children.Add(fovCircle);

        // Crosshair
        AddLine(cx - 8, cy, cx + 8, cy, Color.FromArgb(200, 255, 75, 75), 1);
        AddLine(cx, cy - 8, cx, cy + 8, Color.FromArgb(200, 255, 75, 75), 1);

        // Dead zones
        if (_engine.DeadZoneEnabled)
        {
            var dzCols = new[] { "#FF3300","#FF6600","#FFAA00","#FFD700","#00AAFF" };
            int di = 0;
            foreach (var z in _engine.DeadZones.Where(z => z.Show))
            {
                var (x1, y1, x2, y2) = z.Rect();
                var col = (Color)ColorConverter.ConvertFromString(dzCols[di++ % dzCols.Length]);
                var rect = new Rectangle
                {
                    Width = x2 - x1, Height = y2 - y1,
                    Stroke = new SolidColorBrush(col),
                    StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 4 },
                    Fill = new SolidColorBrush(Color.FromArgb(20, col.R, col.G, col.B))
                };
                Canvas.SetLeft(rect, x1); Canvas.SetTop(rect, y1);
                _canvas.Children.Add(rect);
            }
        }
    }

    private void AddLine(double x1, double y1, double x2, double y2, Color col, double thick)
    {
        _canvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush(col), StrokeThickness = thick
        });
    }

    public void UpdateFov() => Dispatcher.Invoke(BuildFovShape);

    private readonly List<UIElement> _dyn = [];

    private void Redraw()
    {
        foreach (var el in _dyn) _canvas.Children.Remove(el);
        _dyn.Clear();

        var tgt = _engine.LastTarget;
        float cx = _engine.ScreenCx, cy = _engine.ScreenCy;
        float r  = _engine.FovRadius;

        // Detection boxes
        foreach (var d in _engine.LastDetections)
        {
            bool isTgt = tgt != null && MathF.Abs(d.Cx - tgt.Cx) < 10;
            var  col   = isTgt ? Color.FromRgb(0, 255, 157) : Color.FromRgb(255, 75, 75);

            var box = new Rectangle
            {
                Width  = d.X2 - d.X1, Height = d.Y2 - d.Y1,
                Stroke = new SolidColorBrush(col),
                StrokeThickness = isTgt ? 2 : 1,
                Fill   = new SolidColorBrush(Color.FromArgb(isTgt ? (byte)15 : (byte)5, col.R, col.G, col.B))
            };
            Canvas.SetLeft(box, d.X1); Canvas.SetTop(box, d.Y1);
            _canvas.Children.Add(box); _dyn.Add(box);

            var lbl = new TextBlock
            {
                Text = $"{d.Conf:P0}",
                Foreground = new SolidColorBrush(col),
                FontFamily = new FontFamily("Consolas"), FontSize = 8
            };
            Canvas.SetLeft(lbl, d.X1 + 2); Canvas.SetTop(lbl, d.Y1 + 2);
            _canvas.Children.Add(lbl); _dyn.Add(lbl);

            // Линия от центра до цели (только внутри FOV)
            if (d.Dist <= _engine.FovRadius)
            {
                var lineCol = Color.FromArgb(isTgt ? (byte)60 : (byte)18, col.R, col.G, col.B);
                AddDyn(new Line
                {
                    X1 = cx, Y1 = cy, X2 = d.Cx, Y2 = d.Cy,
                    Stroke = new SolidColorBrush(lineCol),
                    StrokeThickness = isTgt ? 1.0 : 0.5,
                    StrokeDashArray = isTgt ? null : new DoubleCollection { 4, 8 }
                }, 0, 0);
            }
        }

        // Aim dot
        if (tgt != null)
        {
            float ax = tgt.AimX, ay = tgt.AimY + _engine.AimYOffsetPx;
            AddDyn(new Ellipse
            {
                Width = 6, Height = 6,
                Fill = new SolidColorBrush(Color.FromRgb(255, 204, 0))
            }, ax - 3, ay - 3);
        }

        // Status line
        string status = tgt != null ? "LOCKED" : "SCANNING";
        var stColor   = tgt != null ? Color.FromRgb(0, 255, 157) : Color.FromRgb(80, 80, 80);
        var stTxt = new TextBlock
        {
            Text       = $"{_engine.Fps:F0} FPS  [{_engine.ProviderName}]  {status}",
            Foreground = new SolidColorBrush(stColor),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(stTxt, cx - 130); Canvas.SetTop(stTxt, cy - r - 32);
        _canvas.Children.Add(stTxt); _dyn.Add(stTxt);
    }

    private void AddDyn(UIElement el, double left, double top)
    {
        Canvas.SetLeft(el, left); Canvas.SetTop(el, top);
        _canvas.Children.Add(el); _dyn.Add(el);
    }

    public new void Show()      { _visible = true;  Visibility = Visibility.Visible; _timer.Start(); }
    public new void Hide()  { _visible = false; Visibility = Visibility.Hidden;  _timer.Stop();  }
    public void Toggle()    { if (_visible) Hide(); else Show(); }
}

// ═══════════════════════════════════════════════════════════════════════════════
// IndicatorWindow — независимый HUD (триггербот + пресет)
// • Всегда поверх игры, полностью некликабельный (WS_EX_TRANSPARENT)
// • Позиция задаётся через меню программы (IndicatorX / IndicatorY в конфиге)
// • Можно включить/выключить через чекбокс в настройках
// • Полностью независим от FOV-оверлея
// ═══════════════════════════════════════════════════════════════════════════════
public sealed class IndicatorWindow : Window
{
    private static readonly Dictionary<string, (Color bg, Color border, Color dot, string icon, string txt, Color tc)> States = new()
    {
        ["OFF"]  = (Color.FromRgb(18,18,26),  Color.FromRgb(55,55,70),   Color.FromRgb(80,80,80),   "○", "ВЫКЛ",  Color.FromRgb(100,100,100)),
        ["ON"]   = (Color.FromRgb(8,28,16),   Color.FromRgb(0,180,90),   Color.FromRgb(0,220,110),  "●", "ТРИГ",  Color.FromRgb(0,255,136)),
        ["AIM"]  = (Color.FromRgb(8,18,30),   Color.FromRgb(0,140,200),  Color.FromRgb(0,180,240),  "◎", "АИМ",   Color.FromRgb(0,210,255)),
        ["FIRE"] = (Color.FromRgb(35,6,6),    Color.FromRgb(220,40,0),   Color.FromRgb(255,60,0),   "◉", "ОГОНЬ", Color.FromRgb(255,80,20)),
    };

    private static readonly string[] PresetColors =
        ["#3A7BD5","#00CC66","#FF8C00","#FF4B4B","#AA44FF","#00AAFF","#FFD700","#FF44AA"];

    [DllImport("user32.dll")] static extern int  SetWindowLong(IntPtr h, int idx, int style);
    [DllImport("user32.dll")] static extern int  GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

    private readonly Border    _root;
    private readonly TextBlock _dotTb;
    private readonly TextBlock _stateTb;
    private readonly TextBlock _presetName;
    private readonly TextBlock _presetBind;

    public IndicatorWindow()
    {
        WindowStyle        = WindowStyle.None;
        ResizeMode         = ResizeMode.NoResize;
        SizeToContent      = SizeToContent.WidthAndHeight;
        Topmost            = true;
        AllowsTransparency = true;            // per-pixel alpha → WS_EX_TRANSPARENT работает корректно
        Background         = Brushes.Transparent; // фон окна прозрачный, контент рисует Border
        IsHitTestVisible   = false;           // WPF-уровень: отключаем hit-test
        ShowInTaskbar      = false;
        Left               = 20;
        Top                = 20;

        _dotTb = new TextBlock
        {
            FontSize  = 14, FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin    = new Thickness(0, 0, 5, 0),
        };
        _stateTb = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _presetBind = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), Opacity = 0.6,
        };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 3) };
        topRow.Children.Add(_dotTb);
        topRow.Children.Add(_stateTb);
        topRow.Children.Add(_presetBind);

        var divider = new Rectangle { Height = 1, Margin = new Thickness(6, 0, 6, 0),
            Fill = new SolidColorBrush(Color.FromRgb(40, 40, 55)) };

        _presetName = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12, 3, 12, 7), TextAlignment = TextAlignment.Center,
        };

        var inner = new StackPanel();
        inner.Children.Add(topRow);
        inner.Children.Add(divider);
        inner.Children.Add(_presetName);

        _root = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = inner, MinWidth = 130 };
        Content = _root;

        // Применяем стили после загрузки окна
        Loaded += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE   = -20;
                const int WS_EX_TRANS   = 0x00000020;
                const int WS_EX_LAYERED = 0x00080000;
                const int WS_EX_TOOLWIN = 0x00000080;
                SetWindowLong(hwnd, GWL_EXSTYLE,
                    GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANS | WS_EX_LAYERED | WS_EX_TOOLWIN);
                // SetLayeredWindowAttributes убран — конфликтует с per-pixel alpha WPF
            }
            catch { }
        };

        UpdateState("OFF", "", "");
    }

    public void UpdateState(string state, string preset, string bind)
    {
        if (!States.TryGetValue(state, out var s)) s = States["OFF"];

        _root.Background  = new SolidColorBrush(s.bg);
        _root.BorderBrush = new SolidColorBrush(s.border);
        _dotTb.Text       = s.icon;
        _dotTb.Foreground = new SolidColorBrush(s.dot);
        _stateTb.Text     = s.txt;
        _stateTb.Foreground = new SolidColorBrush(s.tc);

        if (!string.IsNullOrEmpty(preset))
        {
            int pidx = Math.Abs(preset.GetHashCode()) % PresetColors.Length;
            var pc   = (Color)ColorConverter.ConvertFromString(PresetColors[pidx]);
            _presetName.Text       = preset;
            _presetName.Foreground = new SolidColorBrush(pc);
            _presetBind.Text       = string.IsNullOrEmpty(bind) ? "" : $"[{bind.ToUpper()}]";
            _presetBind.Foreground = new SolidColorBrush(pc);
        }
        else
        {
            _presetName.Text       = "нет пресета";
            _presetName.Foreground = new SolidColorBrush(Color.FromRgb(50, 50, 65));
            _presetBind.Text       = "";
        }
    }

    public void SetAlpha(float alpha)
    {
        Opacity = Math.Clamp(alpha, 0.1, 1.0);
    }

    public void SetPosition(int x, int y)
    {
        Left = Math.Max(0, x);
        Top  = Math.Max(0, y);
    }
}
