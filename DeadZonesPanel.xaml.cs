// UI/DeadZonesPanel.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HachBobAI.Config;

namespace HachBobAI.UI;

public partial class DeadZonesPanel : UserControl
{
    public static readonly DependencyProperty GroupsProperty =
        DependencyProperty.Register(nameof(Groups), typeof(List<DeadZoneGroup>), typeof(DeadZonesPanel),
            new PropertyMetadata(null, (d, _) => ((DeadZonesPanel)d).RebuildAll()));

    public static readonly DependencyProperty ActiveGroupIndexProperty =
        DependencyProperty.Register(nameof(ActiveGroupIndex), typeof(int), typeof(DeadZonesPanel),
            new PropertyMetadata(0, (d, _) => ((DeadZonesPanel)d).RebuildAll()));

    public List<DeadZoneGroup>? Groups          { get => (List<DeadZoneGroup>?)GetValue(GroupsProperty);   set => SetValue(GroupsProperty, value); }
    public int                  ActiveGroupIndex { get => (int)GetValue(ActiveGroupIndexProperty);          set => SetValue(ActiveGroupIndexProperty, value); }

    public event EventHandler?    ZoneChanged;
    public event EventHandler<int>? GroupSwitched;

    private static readonly string[] ZoneColors  = ["#FF3300","#FF6600","#FFAA00","#FFD700","#00AAFF","#AA00FF","#00FF88","#FF00AA"];
    private static readonly string[] GroupColors = ["#3A7BD5","#00CC66","#FF8C00","#FF4B4B","#AA44FF","#00AAFF","#FFD700","#FF44AA"];

    public DeadZonesPanel() { InitializeComponent(); }

    private void RebuildAll()
    {
        PART_Groups.Children.Clear();
        PART_List.Children.Clear();
        var groups = Groups;
        if (groups == null || groups.Count == 0) return;

        // Вкладки групп
        for (int gi = 0; gi < groups.Count; gi++)
            PART_Groups.Children.Add(BuildGroupTab(gi, groups[gi]));

        // Кнопка + Группа
        var addGrpBtn = new Button
        {
            Content = "+ Группа", Height = 28, Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11, Background = new SolidColorBrush(Color.FromRgb(20,20,40)),
            Foreground = new SolidColorBrush(Color.FromRgb(100,100,180)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(50,50,100)),
            BorderThickness = new Thickness(1), Margin = new Thickness(4,0,0,0),
        };
        addGrpBtn.Click += AddGroup_Click;
        PART_Groups.Children.Add(addGrpBtn);

        // Зоны активной группы
        int ai = Math.Clamp(ActiveGroupIndex, 0, groups.Count - 1);
        RebuildZones(groups[ai]);
    }

    private UIElement BuildGroupTab(int gi, DeadZoneGroup group)
    {
        bool isActive = gi == ActiveGroupIndex;
        var col = (Color)ColorConverter.ConvertFromString(GroupColors[gi % GroupColors.Length]);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,2,0) };

        var tabBtn = new Button
        {
            Height = 28, MinWidth = 80, Padding = new Thickness(0),
            FontSize = 11, FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
            Background  = new SolidColorBrush(isActive ? Color.FromArgb(60,col.R,col.G,col.B) : Color.FromRgb(18,18,28)),
            Foreground  = new SolidColorBrush(isActive ? col : Color.FromRgb(100,100,100)),
            BorderBrush = new SolidColorBrush(isActive ? col : Color.FromRgb(45,45,55)),
            BorderThickness = new Thickness(1,1,1, isActive ? 0 : 1),
        };

        var nameBox = new TextBox
        {
            Text = group.Name, FontSize = 11, FontWeight = tabBtn.FontWeight,
            Background = Brushes.Transparent, Foreground = tabBtn.Foreground,
            BorderThickness = new Thickness(0), Padding = new Thickness(8,0,4,0),
            MinWidth = 60, VerticalAlignment = VerticalAlignment.Center,
        };
        nameBox.TextChanged += (_, _) => { group.Name = nameBox.Text; ZoneChanged?.Invoke(this, EventArgs.Empty); };
        nameBox.GotFocus    += (_, _) => { if (ActiveGroupIndex != gi) SwitchGroup(gi); };

        var delGrpBtn = new Button
        {
            Content = "✕", Width = 18, Height = 18, FontSize = 9, Padding = new Thickness(0),
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(120,50,50)),
            BorderThickness = new Thickness(0), Margin = new Thickness(0,0,4,0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Groups?.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        delGrpBtn.Click += (_, _) => DeleteGroup(gi);

        tabBtn.Content = nameBox;
        tabBtn.Click   += (_, _) => { if (ActiveGroupIndex != gi) SwitchGroup(gi); };

        panel.Children.Add(tabBtn);
        panel.Children.Add(delGrpBtn);
        return panel;
    }

    private void RebuildZones(DeadZoneGroup group)
    {
        PART_List.Children.Clear();
        if (group.Zones.Count == 0)
        {
            PART_List.Children.Add(new TextBlock
            {
                Text = "Нет зон. Нажми '+ Добавить зону'",
                Foreground = new SolidColorBrush(Color.FromRgb(85,85,85)),
                FontSize = 11, Margin = new Thickness(0,8,0,8),
            });
            return;
        }
        for (int i = 0; i < group.Zones.Count; i++)
            PART_List.Children.Add(BuildZoneRow(group, i, group.Zones[i]));
    }

    private UIElement BuildZoneRow(DeadZoneGroup group, int idx, DeadZoneEntry zone)
    {
        var col = (Color)ColorConverter.ConvertFromString(ZoneColors[idx % ZoneColors.Length]);
        var colBrush = new SolidColorBrush(col);
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(26,10,10)),
            BorderBrush = colBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Margin = new Thickness(0,3,0,3), Padding = new Thickness(10,8,10,8),
        };
        var stack = new StackPanel();
        var head  = new WrapPanel { Margin = new Thickness(0,0,0,6) };
        head.Children.Add(new TextBlock { Text="■", Foreground=colBrush, FontSize=14, Margin=new Thickness(0,0,6,0), VerticalAlignment=VerticalAlignment.Center });

        var nameBox = new TextBox { Text=zone.Label, Width=120, FontSize=12, Background=new SolidColorBrush(Color.FromRgb(10,5,5)), Foreground=colBrush, BorderBrush=colBrush, Margin=new Thickness(0,0,8,0) };
        nameBox.TextChanged += (_, _) => { zone.Label = nameBox.Text; ZoneChanged?.Invoke(this, EventArgs.Empty); };
        head.Children.Add(nameBox);

        var enCb = new CheckBox { Content="вкл", IsChecked=zone.Enabled, Foreground=colBrush, FontSize=11, Margin=new Thickness(0,0,8,0), VerticalAlignment=VerticalAlignment.Center };
        enCb.Click += (_, _) => { zone.Enabled = enCb.IsChecked == true; ZoneChanged?.Invoke(this, EventArgs.Empty); };
        head.Children.Add(enCb);

        var shCb = new CheckBox { Content="показ", IsChecked=zone.Show, Foreground=new SolidColorBrush(Color.FromRgb(85,85,85)), FontSize=11, Margin=new Thickness(0,0,8,0), VerticalAlignment=VerticalAlignment.Center };
        shCb.Click += (_, _) => { zone.Show = shCb.IsChecked == true; ZoneChanged?.Invoke(this, EventArgs.Empty); };
        head.Children.Add(shCb);

        var delBtn = new Button { Content="✕", Width=28, Height=24, FontSize=12, Background=new SolidColorBrush(Color.FromRgb(58,0,0)), Foreground=new SolidColorBrush(Color.FromRgb(255,85,85)), BorderBrush=new SolidColorBrush(Color.FromRgb(102,0,0)), BorderThickness=new Thickness(1) };
        delBtn.Click += (_, _) => { group.Zones.RemoveAt(idx); RebuildAll(); ZoneChanged?.Invoke(this, EventArgs.Empty); };
        head.Children.Add(delBtn);
        stack.Children.Add(head);

        var coords = new WrapPanel();
        foreach (var (label, getter, setter) in new (string, Func<int>, Action<int>)[]
        {
            ("X", ()=>zone.X, v=>zone.X=v), ("Y", ()=>zone.Y, v=>zone.Y=v),
            ("W", ()=>zone.W, v=>zone.W=v), ("H", ()=>zone.H, v=>zone.H=v),
        })
        {
            var cell  = new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,0,12,0) };
            cell.Children.Add(new TextBlock { Text=label, Foreground=colBrush, FontSize=10, FontWeight=FontWeights.Bold, Width=14, VerticalAlignment=VerticalAlignment.Center });
            var tb = new TextBox { Text=getter().ToString(), Width=70, Background=new SolidColorBrush(Color.FromRgb(10,5,5)), Foreground=new SolidColorBrush(Color.FromRgb(68,136,68)), BorderBrush=new SolidColorBrush(Color.FromRgb(42,16,16)), FontFamily=new FontFamily("Consolas"), FontSize=11, Padding=new Thickness(2) };
            void apply(object? _1, RoutedEventArgs _2) { if (int.TryParse(tb.Text, out int v)) { setter(v); ZoneChanged?.Invoke(this, EventArgs.Empty); } }
            tb.LostFocus += apply;
            tb.KeyDown   += (s,e) => { if (e.Key==System.Windows.Input.Key.Return) apply(s,e); };
            cell.Children.Add(tb);
            int step = label is "W" or "H" ? 10 : 5;
            var arrows = new StackPanel();
            var up = new Button { Content="▲", Width=22, Height=14, FontSize=8, Padding=new Thickness(0), Background=new SolidColorBrush(Color.FromRgb(42,16,16)), Foreground=new SolidColorBrush(Color.FromRgb(255,136,136)), BorderThickness=new Thickness(1), BorderBrush=new SolidColorBrush(Color.FromRgb(58,16,16)) };
            var dn = new Button { Content="▼", Width=22, Height=14, FontSize=8, Padding=new Thickness(0), Background=new SolidColorBrush(Color.FromRgb(42,16,16)), Foreground=new SolidColorBrush(Color.FromRgb(255,136,136)), BorderThickness=new Thickness(1), BorderBrush=new SolidColorBrush(Color.FromRgb(58,16,16)) };
            up.Click += (_, _) => { setter(getter()+step);           tb.Text=getter().ToString(); ZoneChanged?.Invoke(this, EventArgs.Empty); };
            dn.Click += (_, _) => { setter(Math.Max(0,getter()-step)); tb.Text=getter().ToString(); ZoneChanged?.Invoke(this, EventArgs.Empty); };
            arrows.Children.Add(up); arrows.Children.Add(dn);
            cell.Children.Add(arrows);
            coords.Children.Add(cell);
        }
        stack.Children.Add(coords);
        card.Child = stack;
        return card;
    }

    private void SwitchGroup(int gi)
    {
        ActiveGroupIndex = gi;
        GroupSwitched?.Invoke(this, gi);
    }

    private void DeleteGroup(int gi)
    {
        if (Groups == null || Groups.Count <= 1) return;
        Groups.RemoveAt(gi);
        int newActive = Math.Clamp(ActiveGroupIndex, 0, Groups.Count - 1);
        ActiveGroupIndex = newActive;
        GroupSwitched?.Invoke(this, newActive);
        RebuildAll();
        ZoneChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Groups == null) return;
        Groups.Add(new DeadZoneGroup { Name = $"Группа {Groups.Count + 1}", Zones = [] });
        SwitchGroup(Groups.Count - 1);
        ZoneChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddZone_Click(object sender, RoutedEventArgs e)
    {
        if (Groups == null || Groups.Count == 0) return;
        int ai = Math.Clamp(ActiveGroupIndex, 0, Groups.Count - 1);
        var group = Groups[ai];
        int sw = (int)SystemParameters.PrimaryScreenWidth;
        int sh = (int)SystemParameters.PrimaryScreenHeight;
        group.Zones.Add(new DeadZoneEntry { X=sw/2-100, Y=sh/2-100, W=200, H=200, Enabled=true, Show=true, Label=$"Зона {group.Zones.Count+1}" });
        RebuildAll();
        ZoneChanged?.Invoke(this, EventArgs.Empty);
    }
}
