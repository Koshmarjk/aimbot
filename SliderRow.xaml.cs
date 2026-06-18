// UI/SliderRow.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HachBobAI.UI;

public partial class SliderRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderRow), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(SliderRow),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinProperty =
        DependencyProperty.Register(nameof(Min), typeof(double), typeof(SliderRow), new PropertyMetadata(0.0, OnRangeChanged));

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.Register(nameof(Max), typeof(double), typeof(SliderRow), new PropertyMetadata(1.0, OnRangeChanged));

    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(SliderRow), new PropertyMetadata("F2"));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(SliderRow), new PropertyMetadata(""));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Min   { get => (double)GetValue(MinProperty);   set => SetValue(MinProperty, value); }
    public double Max   { get => (double)GetValue(MaxProperty);   set => SetValue(MaxProperty, value); }
    public string Format{ get => (string)GetValue(FormatProperty);set => SetValue(FormatProperty, value); }
    public string Unit  { get => (string)GetValue(UnitProperty);  set => SetValue(UnitProperty, value); }

    public event RoutedPropertyChangedEventHandler<double>? ValueChanged;

    private bool _lock;

    public SliderRow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Применяем значения после того как PART_Slider создан
            if (Min <= Max)
            {
                PART_Slider.Minimum = Min;
                PART_Slider.Maximum = Max;
            }
            else
            {
                PART_Slider.Maximum = Max;
                PART_Slider.Minimum = Min;
            }
            PART_Slider.Value = Math.Clamp(Value, Min, Max);
            SyncEntry();
        };
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (SliderRow)d;
        if (s.PART_Slider == null || s._lock) return;
        s._lock = true;
        s.PART_Slider.Value = Math.Clamp((double)e.NewValue, s.Min, s.Max);
        s.SyncEntry();
        s._lock = false;
        s.ValueChanged?.Invoke(s, new RoutedPropertyChangedEventArgs<double>((double)e.OldValue, (double)e.NewValue));
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (SliderRow)d;
        if (s.PART_Slider == null) return;
        double safeMin = Math.Min(s.Min, s.Max);
        double safeMax = Math.Max(s.Min, s.Max);
        s.PART_Slider.Minimum = safeMin;
        s.PART_Slider.Maximum = safeMax;
        s.PART_Slider.Value = Math.Clamp(s.Value, safeMin, safeMax);
    }

    private void SyncEntry()
    {
        PART_Entry.Text = Value.ToString(Format);
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_lock) return;
        _lock = true;
        Value = e.NewValue;
        SyncEntry();
        _lock = false;
        ValueChanged?.Invoke(this, e);
    }

    private void Entry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) ApplyEntry();
    }

    private void Entry_LostFocus(object sender, RoutedEventArgs e) => ApplyEntry();

    private void ApplyEntry()
    {
        if (_lock) return;
        if (!double.TryParse(PART_Entry.Text.Replace("+", "").Trim(), out double v)) return;
        v = Math.Clamp(v, Min, Max);
        _lock = true;
        double old = Value;
        Value = v;
        PART_Slider.Value = v;
        SyncEntry();
        _lock = false;
        ValueChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<double>(old, v));
    }
}
