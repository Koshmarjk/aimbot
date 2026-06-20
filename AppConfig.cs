// Config/AppConfig.cs
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HachBobAI.Config;

public class VisionConfig
{
    [JsonPropertyName("dml_device_id")]  public int DmlDeviceId      { get; set; } = 0;
    [JsonPropertyName("enabled")]        public bool   Enabled       { get; set; } = true;
    [JsonPropertyName("model_path")]     public string ModelPath     { get; set; } = "C:\\Users\\kosta\\OneDrive\\Desktop\\bot\\models\\model_nano_416_op12_v2.onnx";
    [JsonPropertyName("fov_radius")]     public int    FovRadius     { get; set; } = 300;
    [JsonPropertyName("conf")]           public double Conf          { get; set; } = 0.45;
    [JsonPropertyName("detection_class")] public int    DetectionClass { get; set; } = -1; // -1=T/CT, 0=CT, 1=T
    [JsonPropertyName("provider")]       public string Provider      { get; set; } = "tensorrt";
    [JsonPropertyName("use_fp16")]       public bool   UseFp16       { get; set; } = false;
    [JsonPropertyName("strength")]       public double Strength      { get; set; } = 0.15;
    [JsonPropertyName("smooth")]         public double Smooth        { get; set; } = 0.25;
    [JsonPropertyName("prediction")]     public double Prediction    { get; set; } = 0.6;
    [JsonPropertyName("max_step")]       public double MaxStep       { get; set; } = 22.0;
    [JsonPropertyName("stop_dist")]      public double StopDist      { get; set; } = 3.0;
    [JsonPropertyName("show_fov")]       public bool   ShowFov       { get; set; } = true;
    [JsonPropertyName("capture_size")]   public int    CaptureSize   { get; set; } = 320;
    [JsonPropertyName("use_tiled")]       public bool   UseTiled      { get; set; } = false;
    [JsonPropertyName("tile_overlap")]    public int    TileOverlap   { get; set; } = 64;
    [JsonPropertyName("aim_y_offset")]   public double AimYOffset    { get; set; } = 0.0;
    [JsonPropertyName("confirm_frames")] public int    ConfirmFrames { get; set; } = 3;
    [JsonPropertyName("priority_size")]  public bool   PrioritySize  { get; set; } = true;
    [JsonPropertyName("ghost_max_frames")] public int  GhostMaxFrames { get; set; } = 10;
    [JsonPropertyName("aim_hz")]         public int    AimHz         { get; set; } = 0;
    [JsonPropertyName("dead_zone")]      public bool   DeadZone      { get; set; } = true;
    // NUMA-пиннинг — включать только на Dual Xeon / многосокетных серверах.
    // На обычном десктопе с одним CPU оставить false — иначе лаги и -100 FPS.
    [JsonPropertyName("numa_pinning")]   public bool   NumaPinning   { get; set; } = false;
    // Количество ядер Socket 0 (0 = авто: ProcessorCount / 2)
    [JsonPropertyName("numa_cores")]     public int    NumaCores     { get; set; } = 0;
}

public class TriggerbotConfig
{
    [JsonPropertyName("enabled")]   public bool   Enabled  { get; set; } = false;
    [JsonPropertyName("tolerance")] public double Tolerance { get; set; } = 12.0;
    [JsonPropertyName("delay_min")] public double DelayMin { get; set; } = 0.0;
    [JsonPropertyName("delay_max")] public double DelayMax { get; set; } = 0.0;
    [JsonPropertyName("aim_only")]  public bool   AimOnly  { get; set; } = true;
}

public class RangefinderConfig
{
    [JsonPropertyName("enabled")] public bool                   Enabled { get; set; } = false;
    [JsonPropertyName("table")]   public List<RangefinderEntry> Table   { get; set; } = [];
}

public class RangefinderEntry
{
    [JsonPropertyName("bbox_h")]   public double BboxH   { get; set; }
    [JsonPropertyName("y_offset")] public double YOffset { get; set; }
}

public class BindsConfig
{
    [JsonPropertyName("aim")]           public string Aim          { get; set; } = "x2";
    [JsonPropertyName("switch_target")] public string SwitchTarget { get; set; } = "x1";
    [JsonPropertyName("toggle")]        public string Toggle       { get; set; } = "insert";
    [JsonPropertyName("overlay")]       public string Overlay      { get; set; } = "f4";
    [JsonPropertyName("hide_gui")]      public string HideGui      { get; set; } = "home";
    [JsonPropertyName("dead_zone")]     public string DeadZone     { get; set; } = "v";
    [JsonPropertyName("triggerbot")]    public string Triggerbot   { get; set; } = "\\";
    [JsonPropertyName("class_toggle")]  public string ClassToggle  { get; set; } = "t";
    [JsonPropertyName("rangefinder")]   public string Rangefinder  { get; set; } = "r";
    [JsonPropertyName("exit")]          public string Exit         { get; set; } = "f12";
}

public class DeadZoneEntry
{
    [JsonPropertyName("x")]       public int    X       { get; set; }
    [JsonPropertyName("y")]       public int    Y       { get; set; }
    [JsonPropertyName("w")]       public int    W       { get; set; }
    [JsonPropertyName("h")]       public int    H       { get; set; }
    [JsonPropertyName("enabled")] public bool   Enabled { get; set; } = true;
    [JsonPropertyName("show")]    public bool   Show    { get; set; } = true;
    [JsonPropertyName("label")]   public string Label   { get; set; } = "";
}

public class PresetConfig
{
    [JsonPropertyName("name")]                public string Name               { get; set; } = "Пресет";
    [JsonPropertyName("bind")]                public string Bind               { get; set; } = "";
    [JsonPropertyName("fov_radius")]          public double FovRadius          { get; set; } = 300;
    [JsonPropertyName("conf")]                public double Conf               { get; set; } = 0.45;
    [JsonPropertyName("strength")]            public double Strength           { get; set; } = 0.15;
    [JsonPropertyName("max_step")]            public double MaxStep            { get; set; } = 18.0;
    [JsonPropertyName("smooth")]              public double Smooth             { get; set; } = 0.75;
    [JsonPropertyName("prediction")]          public double Prediction         { get; set; } = 0.4;
    [JsonPropertyName("aim_y_offset")]        public double AimYOffset         { get; set; } = 0.0;
    [JsonPropertyName("confirm_frames")]      public double ConfirmFrames      { get; set; } = 1;
    [JsonPropertyName("stop_dist")]           public double StopDist           { get; set; } = 2.5;
    [JsonPropertyName("tb_tolerance")]        public double TbTolerance        { get; set; } = 50.0;
    [JsonPropertyName("tb_delay_min")]        public double TbDelayMin         { get; set; } = 0.0;
    [JsonPropertyName("tb_delay_max")]        public double TbDelayMax         { get; set; } = 0.0;
    [JsonPropertyName("tb_aim_only")]         public bool   TbAimOnly          { get; set; } = true;
    [JsonPropertyName("tb_enabled")]          public bool   TbEnabled          { get; set; } = false;
    [JsonPropertyName("rangefinder_enabled")] public bool   RangefinderEnabled { get; set; } = false;
    [JsonPropertyName("rangefinder")]         public List<RangefinderEntry> Rangefinder { get; set; } = [];
}

public class DeadZoneGroup
{
    [JsonPropertyName("name")]  public string              Name  { get; set; } = "Группа";
    [JsonPropertyName("zones")] public List<DeadZoneEntry> Zones { get; set; } = [];
}

public class AppConfig
{
    [JsonPropertyName("volume")]                 public double             Volume              { get; set; } = 20;
    [JsonPropertyName("indicator_alpha")]        public double             IndicatorAlpha      { get; set; } = 0.7;
    [JsonPropertyName("indicator_x")]            public int                IndicatorX          { get; set; } = 20;
    [JsonPropertyName("indicator_y")]            public int                IndicatorY          { get; set; } = 20;
    [JsonPropertyName("indicator_enabled")]      public bool               IndicatorEnabled    { get; set; } = true;
    [JsonPropertyName("toggle_mode")]            public bool               ToggleMode          { get; set; } = false;
    [JsonPropertyName("active_preset")]          public int                ActivePreset        { get; set; } = -1;
    [JsonPropertyName("active_dead_zone_group")] public int                ActiveDeadZoneGroup { get; set; } = 0;
    [JsonPropertyName("vision")]                 public VisionConfig       Vision              { get; set; } = new();
    [JsonPropertyName("triggerbot")]             public TriggerbotConfig   Triggerbot          { get; set; } = new();
    [JsonPropertyName("rangefinder")]            public RangefinderConfig  Rangefinder         { get; set; } = new();
    [JsonPropertyName("binds")]                  public BindsConfig        Binds               { get; set; } = new();
    [JsonPropertyName("dead_zone_groups")]       public List<DeadZoneGroup> DeadZoneGroups     { get; set; } = DefaultDeadZoneGroups();
    [JsonPropertyName("presets")]                public List<PresetConfig> Presets             { get; set; } = DefaultPresets();

    // Временное поле для миграции со старого формата — не сохраняем
    [JsonPropertyName("dead_zones_list")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<DeadZoneEntry>? DeadZonesList { get; set; } = null;

    /// <summary>Зоны текущей активной группы</summary>
    public List<DeadZoneEntry> CurrentZones =>
        DeadZoneGroups.Count > 0 && ActiveDeadZoneGroup >= 0 && ActiveDeadZoneGroup < DeadZoneGroups.Count
            ? DeadZoneGroups[ActiveDeadZoneGroup].Zones
            : [];

    /// <summary>Название текущей группы</summary>
    public string CurrentGroupName =>
        DeadZoneGroups.Count > 0 && ActiveDeadZoneGroup >= 0 && ActiveDeadZoneGroup < DeadZoneGroups.Count
            ? DeadZoneGroups[ActiveDeadZoneGroup].Name
            : "—";

    public static List<DeadZoneGroup> DefaultDeadZoneGroups() =>
        [new() { Name = "Группа 1", Zones = [] }];

    public static List<PresetConfig> DefaultPresets() =>
    [
        new() { Name="ПП",    Bind="f1", FovRadius=200, Conf=0.40, Strength=0.22, MaxStep=18.0, Smooth=0.75, Prediction=0.4,  AimYOffset=0.0,  ConfirmFrames=1, StopDist=2.5, TbTolerance=50,  TbAimOnly=true,  RangefinderEnabled=false },
        new() { Name="ШВ",    Bind="f2", FovRadius=250, Conf=0.38, Strength=0.20, MaxStep=20.0, Smooth=0.70, Prediction=0.5,  AimYOffset=0.0,  ConfirmFrames=1, StopDist=2.5, TbTolerance=55,  TbAimOnly=true,  RangefinderEnabled=false },
        new() { Name="МВ",    Bind="f3", FovRadius=280, Conf=0.38, Strength=0.18, MaxStep=22.0, Smooth=0.65, Prediction=0.6,  AimYOffset=0.0,  ConfirmFrames=2, StopDist=3.0, TbTolerance=60,  TbAimOnly=true,  RangefinderEnabled=false },
        new() { Name="СВ",    Bind="f4", FovRadius=350, Conf=0.42, Strength=0.12, MaxStep=14.0, Smooth=0.55, Prediction=0.9,  AimYOffset=-5.0, ConfirmFrames=3, StopDist=4.0, TbTolerance=35,  TbAimOnly=true,  RangefinderEnabled=true  },
        new() { Name="Дробь", Bind="f5", FovRadius=160, Conf=0.35, Strength=0.30, MaxStep=28.0, Smooth=0.85, Prediction=0.2,  AimYOffset=0.0,  ConfirmFrames=0, StopDist=2.0, TbTolerance=70,  TbAimOnly=true,  RangefinderEnabled=false },
    ];
}

public static class ConfigManager
{
    // Ищем корневую папку проекта (где лежит config.json или .csproj).
    // При dotnet run BaseDirectory = bin/Debug/net8.0-windows/ — идём вверх.
    // При запуске exe напрямую из корня — остаёмся там.
    private static readonly string BaseDir = ResolveBaseDir();

    private static string ResolveBaseDir()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        // Поднимаемся максимум на 4 уровня вверх, пока не найдём config.json или .csproj
        for (int i = 0; i < 4; i++)
        {
            if (File.Exists(Path.Combine(dir, "config.json")) ||
                Directory.GetFiles(dir, "*.csproj").Length > 0)
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }
        // Fallback — рядом с exe
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    private static readonly string ConfigFile  = Path.Combine(BaseDir, "config.json");
    private static readonly string PresetsFile = Path.Combine(BaseDir, "presets.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigFile)) return new AppConfig();
        try
        {
            var json = File.ReadAllText(ConfigFile);
            var cfg  = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();

            // Миграция: если нет групп но есть старый dead_zones_list — переносим
            if (cfg.DeadZoneGroups.Count == 0 ||
               (cfg.DeadZoneGroups.Count == 1 && cfg.DeadZoneGroups[0].Zones.Count == 0
                && cfg.DeadZonesList != null && cfg.DeadZonesList.Count > 0))
            {
                if (cfg.DeadZoneGroups.Count == 0)
                    cfg.DeadZoneGroups.Add(new DeadZoneGroup { Name = "Группа 1" });
                cfg.DeadZoneGroups[0].Zones = cfg.DeadZonesList ?? [];
                Console.WriteLine($"[config] Migrated {cfg.DeadZoneGroups[0].Zones.Count} dead zones → Группа 1");
            }
            cfg.DeadZonesList = null; // очищаем после миграции

            // Зажимаем ActiveDeadZoneGroup в допустимые пределы
            cfg.ActiveDeadZoneGroup = Math.Clamp(cfg.ActiveDeadZoneGroup, 0,
                Math.Max(0, cfg.DeadZoneGroups.Count - 1));

            return cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Load error: {ex.Message}");
            return new AppConfig();
        }
    }

    public static void Save(AppConfig cfg)
    {
        AtomicWrite(ConfigFile, cfg);
        Console.WriteLine($"[config] Saved → {ConfigFile}");
    }

    public static List<PresetConfig>? LoadPresets()
    {
        if (!File.Exists(PresetsFile)) return null;
        try
        {
            var json = File.ReadAllText(PresetsFile);
            return JsonSerializer.Deserialize<List<PresetConfig>>(json, JsonOpts);
        }
        catch { return null; }
    }

    public static void SavePresets(List<PresetConfig> presets)
    {
        AtomicWrite(PresetsFile, presets);
        Console.WriteLine($"[config] Presets → {PresetsFile}");
    }

    private static void AtomicWrite<T>(string path, T data)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
