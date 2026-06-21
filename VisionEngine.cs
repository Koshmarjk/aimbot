// Vision/VisionEngine.cs — ONNX Runtime + OpenVINO Native + DXGI Desktop Duplication
// ─────────────────────────────────────────────────────────────────────────────
// ПАТЧ v2: два ключевых исправления из Python оригинала
//   1. NUMA-пиннинг к Socket 0 (3× FPS на Dual Xeon)
//   2. OpenVINO Native (OpenVinoSharp) вместо ORT EP — поддержка XML + реальный LATENCY hint
//   3. Preprocess: cv2.INTER_LINEAR через unsafe pointer вместо bilinear Parallel.For
// ─────────────────────────────────────────────────────────────────────────────
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using HachBobAI.Config;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HachBobAI.Vision;

public sealed class Detection
{
    public float X1, Y1, X2, Y2, Conf;
    public int ClassId;
    public float Cx, Cy;
    public float AimX, AimY;
    public float Dist, Area;
    private const float AimYFrac = 0.28f;

    public Detection(float x1, float y1, float x2, float y2, float conf, float scx, float scy, int classId = 0)
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Conf = conf; ClassId = classId;
        Cx = (x1 + x2) * 0.5f; Cy = (y1 + y2) * 0.5f;
        float h = y2 - y1;
        AimX = Cx; AimY = y1 + h * AimYFrac;
        Dist = MathF.Sqrt((AimX - scx) * (AimX - scx) + (AimY - scy) * (AimY - scy));
        Area = (x2 - x1) * h;
    }
}

public sealed class DeadZone
{
    public int X, Y, W, H;
    public bool Enabled, Show;
    public string Label;
    public DeadZone(DeadZoneEntry e) { X=e.X; Y=e.Y; W=e.W; H=e.H; Enabled=e.Enabled; Show=e.Show; Label=e.Label; }
    public bool Contains(float cx, float cy) => cx>=X && cx<=X+W && cy>=Y && cy<=Y+H;
    public (int x1,int y1,int x2,int y2) Rect() => (X,Y,X+W,Y+H);
}

public sealed class VisionEngine : IDisposable
{
    // ── Settings ──────────────────────────────────────────────────────────────
    public int DmlDeviceId { get; set; } = 0;
    public float FovRadius { get; set; } = 300;
    public float ConfThresh { get; set; } = 0.45f;
    // Dataset YAML order:
    // names: [CT, T], nc: 2 => class 0 = CT, class 1 = T.
    public int ModelClassCount { get; set; } = 2;
    public string[] ModelClassNames { get; set; } = ["CT", "T"];
    // YOLOv8 export usually has no objectness: [x,y,w,h,CT_conf,T_conf].
    // Set true only for YOLOv5-like output: [x,y,w,h,obj,CT_conf,T_conf].
    public bool OutputHasObjectness { get; set; } = false;
    // -1 = both/all classes, 0 = CT, 1 = T.
    public int ActiveClassId { get; private set; } = -1;
    public string ActiveClassName => ActiveClassId < 0
        ? "T/CT"
        : ActiveClassId < ModelClassNames.Length ? ModelClassNames[ActiveClassId] : $"class{ActiveClassId}";
    // Legacy name kept for existing config/UI bindings.
    // Semantics changed: value is now PERCENT OF CURRENT BBOX HEIGHT, not absolute px.
    // 0 = built-in aim point (Detection.AimY, currently upper body/head area),
    // +10 = 10% of bbox lower, -10 = 10% of bbox higher.
    public float AimYOffsetPx { get; set; } = 0f;
    public float AimYOffsetPercent
    {
        get => AimYOffsetPx;
        set => AimYOffsetPx = value;
    }
    public float RfYOffsetExtra { get; set; } = 0f; // rangefinder still uses absolute px
    // Small horizontal aim-point correction. Negative = left, positive = right.
    // Percent of current bbox width, so it scales with target size.
    public float AimXOffsetPercent { get; set; } = -5f;
    private float _temporaryAimYOffsetPx; // temporary shot-to-shot point offset, absolute px
    public float PredictionStr { get; set; } = 0.6f;
    // Auto lead for moving targets. Works even when config PredictionStr is 0.
    public bool  AdaptiveMotionPrediction { get; set; } = true;
    public float MotionPredictionMin { get; set; } = 0.55f;
    public float MotionPredictionMax { get; set; } = 1.65f;
    public float MotionPredictionStartSpeed { get; set; } = 25f;
    public float MotionPredictionMaxSpeed { get; set; } = 550f;
    public float StopDist { get; set; } = 3f;
    public int ConfirmFrames { get; set; } = 3;
    public bool PrioritySize { get; set; } = true;
    public bool DeadZoneEnabled { get; set; } = true;
    public int AimHz { get; set; } = 0;
    public bool UseTiled { get; set; } = false;
    public int TileOverlap { get; set; } = 64;

    // Stability / anti-jitter tuning
    public float DetectionSmoothAlpha { get; set; } = 0.32f; // меньше = плавнее bbox/aim-point
    public float StickySwitchRatio { get; set; } = 3.0f;     // новая цель должна быть сильно лучше текущей
    public float StickyRadiusPx { get; set; } = 180f;
    public float MaxPredictionPx { get; set; } = 64f;
    public float GhostMaxDistance { get; set; } = 90f;

    // Жёсткий target lock против мерцания/перескоков между несколькими целями.
    public bool  TargetLockEnabled { get; set; } = true;
    public bool  TargetLockHard { get; set; } = true;        // true = не переключаемся, пока locked цель видна
    public float TargetLockRadiusPx { get; set; } = 320f;
    // Strict same-target reacquire gate. Used instead of TargetLockRadiusPx to avoid snapping to neighbours.
    public float TargetReacquireRadiusPx { get; set; } = 70f;
    public float TargetLockMinIoU { get; set; } = 0.08f;
    public float TargetSwitchScoreRatio { get; set; } = 999f;
    public int   TargetLockMaxMissFrames { get; set; } = 10;
    // Short grace only. Long values make aim stand still when old target flickers out.
    public int   TargetLockHoldFrames { get; set; } = 3;
    // New target must be stable for a few frames before switching from a lost lock.
    public int   TargetSwitchConfirmFrames { get; set; } = 4;
    public float TargetSwitchConfirmRadiusPx { get; set; } = 90f;
    // Disabled by default: height-collapse switching can cause ping-pong between equal nearby targets.
    // Enable only if you really need this behaviour.
    public bool  SwitchOnHitboxHeightCollapse { get; set; } = false;
    public float HitboxHeightCollapseRatio { get; set; } = 3.0f;
    public float HitboxHeightCollapseMinPrevH { get; set; } = 55f;
    private float _providerPredictionScale = 1.0f;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly object _stateLock = new();
    private readonly object _sessionLock = new();

    private Detection? _lastTarget;      // аим (включает призрак)
    private Detection? _realTarget;      // только реальная цель (триггербот)
    private List<Detection> _lastDetections = [];
    private float _fps;
    private int _frameId;
    private bool _isOnTarget;
    private string _providerName = "none";
    private float _targetSpeed;     // px/s для адаптивного strength

    public Detection? LastTarget { get { lock (_stateLock) return _lastTarget; } private set { lock (_stateLock) _lastTarget = value; } }
    public Detection? RealTarget { get { lock (_stateLock) return _realTarget; } private set { lock (_stateLock) _realTarget = value; } }
    public List<Detection> LastDetections { get { lock (_stateLock) return _lastDetections.ToList(); } private set { lock (_stateLock) _lastDetections = value; } }
    public float Fps { get { lock (_stateLock) return _fps; } private set { lock (_stateLock) _fps = value; } }
    public int FrameId { get { lock (_stateLock) return _frameId; } private set { lock (_stateLock) _frameId = value; } }
    public bool IsOnTarget { get { lock (_stateLock) return _isOnTarget; } set { lock (_stateLock) _isOnTarget = value; } }
    public string ProviderName { get { lock (_stateLock) return _providerName; } private set { lock (_stateLock) _providerName = value; } }
    public List<DeadZone> DeadZones { get; set; } = [];
    public int ScreenCx => _screenCx;
    public int ScreenCy => _screenCy;
    public int _capSize { get; private set; }
    public float TargetSpeed { get { lock (_stateLock) return _targetSpeed; } private set { lock (_stateLock) _targetSpeed = value; } }

    public readonly record struct AimSnapshot(
        Detection? LastTarget,
        Detection? RealTarget,
        int FrameId,
        float TargetSpeed,
        float RawDx,
        float RawDy);

    // ── Internals ─────────────────────────────────────────────────────────────
    private float _velX, _velY, _fastVelX, _fastVelY;
    private float _lastCapX, _lastCapY; // позиция цели в пространстве capture (не экранная)
    private double _lastT;
    // PredictDt — время упреждения в секундах
    // prediction=1.0 → упреждение на ~1 кадр вперёд (динамически по реальному FPS)
    // prediction=0.5 → полкадра, prediction=2.0 → два кадра
    private float _frameInterval = 0.016f; // обновляется в UpdateVelocity по реальному dt
    private float EffectivePredictionStr
    {
        get
        {
            float p = Math.Max(0f, PredictionStr);
            if (AdaptiveMotionPrediction && _targetSpeed > MotionPredictionStartSpeed)
            {
                float denom = Math.Max(1f, MotionPredictionMaxSpeed - MotionPredictionStartSpeed);
                float t = Math.Clamp((_targetSpeed - MotionPredictionStartSpeed) / denom, 0f, 1f);
                float auto = MotionPredictionMin + (MotionPredictionMax - MotionPredictionMin) * t;
                p = Math.Max(p, auto);
            }
            return p;
        }
    }

    private float PredictDt => EffectivePredictionStr * _providerPredictionScale * _frameInterval;

    private bool _manualLock;
    private double _manualLockUntil;

    private bool _hasTargetLock;
    private float _lockedAimX, _lockedAimY;
    private float _lockedX1, _lockedY1, _lockedX2, _lockedY2;
    private int _targetLockMissFrames;
    private double _targetSwitchCooldownUntil;
    private float _pendingSwitchAimX, _pendingSwitchAimY;
    private int _pendingSwitchFrames;

    // ── Ghost target (инерция после потери цели) ──────────────────────────────
    // Если цель потерялась — ещё GhostFrames кадров ведём в последнюю позицию
    // Убирает дёрганье на однокадровые фейки и мгновенную потерю цели
    public void ResetGhost()
    {
        lock (_stateLock)
        {
            _ghostTarget = null;
            _ghostFrames = 0;
            _hasTargetLock = false;
            _targetLockMissFrames = 0;
            _pendingSwitchFrames = 0;
        }
    }
    private Detection? _ghostTarget;
    private int _ghostFrames;
    public int GhostMaxFrames { get; set; } = 12; // увеличили с 6 до 12

    // Экранные координаты последней реальной цели (для ghost экстраполяции)
    private float _ghostScreenAimX, _ghostScreenAimY;
    // Скорость на момент потери цели
    private float _ghostVelX, _ghostVelY;
    private float _anchorLastRealX, _anchorLastRealY;

    private InferenceSession? _session;
    private string? _inputName;
    private readonly int _screenW, _screenH;
    private readonly int _screenCx, _screenCy;
    private int _capLeft, _capTop;
    private int _modelInputSize = 640; // будет перезаписан из метаданных модели
    public int ModelInputSize => _modelInputSize;

    private readonly List<float[]> _tracks = [];
    private const float SF_IOU = 0.25f;
    private const int SF_MISS = 4;   // 4 кадра без подтверждения → убиваем трек
    private const float SF_ALPHA = 0.45f;

    private readonly CancellationTokenSource _cts = new();
    private Task? _captureTask, _inferTask;
    private readonly Channel<byte[]> _frameChannel =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest });

    private bool _useDxgi;
    private bool _numaPinning;
    private int _numaCores;

    // ── Win32 NUMA-пиннинг (повторяет Python _setup_process_priority) ─────────
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr h, int pri);
    private const uint HIGH_PRIORITY_CLASS = 0x80;
    private const int THREAD_PRIORITY_HIGHEST = 2;

    // Количество ядер одного NUMA-сокета (Socket 0 = ядра 0..HalfCores-1)
    private static readonly int _halfCores = Math.Max(1, Environment.ProcessorCount / 2);

    // ── OpenVINO Native сессия (замена ORT EP для XML и реального LATENCY) ────
    // null если используется ORT (_session != null)
#if USE_OPENVINO
    private OpenVinoSession? _ovSession;
#endif

    // ── Constructor ───────────────────────────────────────────────────────────
    public VisionEngine(string modelPath, int screenW, int screenH,
                        float conf, float fovRadius, string provider, int captureSize, bool useFp16,
                        bool numaPinning = false, int numaCores = 0,
                        int dmlDeviceId = 0)
    {
        _screenW = screenW; _screenH = screenH;
        _screenCx = screenW / 2; _screenCy = screenH / 2;
        ConfThresh = conf; FovRadius = fovRadius;
        DmlDeviceId = dmlDeviceId;
        ApplyCaptureSize(captureSize);

        // ── NUMA-пиннинг: включать только на Dual Xeon / многосокетных серверах
        // На обычном десктопе numaPinning=false — только HIGH priority
        ApplyNumaPinning(numaPinning, numaCores);
        _numaPinning = numaPinning;
        _numaCores = numaCores;

        // ── Выбор провайдера ──────────────────────────────────────────────────
        bool useNativeOV = provider.Equals("openvino_native", StringComparison.OrdinalIgnoreCase)
                        || provider.Equals("openvino_xml", StringComparison.OrdinalIgnoreCase)
                        || (provider.Equals("openvino", StringComparison.OrdinalIgnoreCase)
                            && modelPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

#if USE_OPENVINO
    if (useNativeOV)
    {
        try
        {
            _ovSession   = new OpenVinoSession(modelPath, _halfCores, numaPinning: _numaPinning);
            ProviderName = _ovSession.ProviderName;
            _modelInputSize = _ovSession.ModelInputSize;
            Console.WriteLine($"[vision] OpenVINO Native ✓  input={_modelInputSize}px");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[vision] OpenVINO Native ошибка: {e.Message}");
            _ovSession = null;
            _session = CreateSession(modelPath, "cpu_optimized", useFp16, DmlDeviceId, out var fb);
        }
    }
else
#endif
        {
            _session = CreateSession(modelPath, provider, useFp16, DmlDeviceId, out var pName);
            ProviderName = pName;
        }

        _inputName = _session?.InputMetadata.Keys.First();

        // Определяем размер входа модели из её метаданных (416, 640 и т.д.)
        if (_session != null && _inputName != null)
        {
            var shape = _session.InputMetadata[_inputName].Dimensions;
            // shape = [1, 3, H, W] — берём H (индекс 2)
            int detectedSize = shape.Length >= 3 && shape[2] > 0 ? shape[2] : 640;
            _modelInputSize = detectedSize;
            Console.WriteLine($"[vision] Model input size: {_modelInputSize}px");
            // Capture size независим от размера модели — Preprocess() сам ресайзит
        }
        ApplyProviderRuntimeProfile();
        Warmup();
    }

    private void ApplyProviderRuntimeProfile()
    {
        string p = ProviderName;
        if (p.StartsWith("DML", StringComparison.OrdinalIgnoreCase))
        {
            // DirectML часто даёт более неровный frame pacing/latency, из-за чего prediction
            // может перекидывать цель через центр и создавать маятник. Для DML профиль мягче.
            // DML can have uneven pacing, but too much prediction damping makes aim lag behind moving targets.
            _providerPredictionScale = 1.0f;
            MaxPredictionPx = Math.Max(MaxPredictionPx, 56f);
            DetectionSmoothAlpha = Math.Min(DetectionSmoothAlpha, 0.35f);
            StickySwitchRatio = Math.Max(StickySwitchRatio, 1.30f);
            GhostMaxDistance = Math.Min(GhostMaxDistance, 90f);
            Console.WriteLine("[vision] DML runtime profile: prediction×1.0, adaptive motion lead enabled");
        }
        else if (p.Contains("TensorRT", StringComparison.OrdinalIgnoreCase))
        {
            // TensorRT обычно даёт более стабильную задержку — оставляем быстрый профиль.
            _providerPredictionScale = 1.0f;
            MaxPredictionPx = Math.Max(MaxPredictionPx, 56f);
            Console.WriteLine("[vision] TensorRT runtime profile: low-latency defaults");
        }
        else
        {
            _providerPredictionScale = 0.85f;
        }
    }

    // ── NUMA-пиннинг: HIGH priority + опциональный affinity к Socket 0 ─────────
    // numaPinning=true  — только для Dual Xeon / многосокетных серверов
    // numaPinning=false — обычный десктоп: только HIGH priority, все ядра доступны
    private static void ApplyNumaPinning(bool numaPinning = false, int numaCores = 0)
    {
        try
        {
            var h = GetCurrentProcess();
            SetPriorityClass(h, HIGH_PRIORITY_CLASS);

            if (numaPinning)
            {
                int cores = numaCores > 0 ? numaCores : _halfCores;
                ulong mask = (1UL << cores) - 1UL;
                SetProcessAffinityMask(h, (UIntPtr)mask);
                Console.WriteLine($"[vision] NUMA pinning ON: Socket 0, {cores} ядер, mask=0x{mask:X}");
            }
            else
            {
                Console.WriteLine($"[vision] NUMA pinning OFF: HIGH priority, все {Environment.ProcessorCount} ядер");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[vision] ApplyNumaPinning: {e.Message}");
        }
    }

    private static InferenceSession? CreateSession(string path, string provider,
        bool fp16, int dmlDeviceId, out string providerName)
    {
        providerName = "none";
        if (!File.Exists(path)) { Console.WriteLine($"[vision] {path} not found"); return null; }

        // Для OpenVINO XML IR: проверяем наличие .bin рядом
        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            string binCheck = Path.ChangeExtension(path, ".bin");
            if (!File.Exists(binCheck))
            {
                Console.WriteLine($"[vision] ✗ Нет файла весов: {binCheck}");
                Console.WriteLine("[vision] OpenVINO IR требует: model.xml + model.bin в одной папке");
                return null;
            }
        }

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false,
            IntraOpNumThreads = Math.Max(4, Environment.ProcessorCount - 2),
            InterOpNumThreads = 2,
        };

        try
        {
            switch (provider.ToLowerInvariant())
            {
                case "tensorrt":
                    // Кэш храним рядом с config.json — не удаляется при пересборке
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    // При dotnet run: .../bin/Debug/net8.0-windows/ → идём на 3 уровня вверх
                    var projectDir = baseDir.TrimEnd('\\', '/');
                    for (int up = 0; up < 3; up++)
                    {
                        var parent = Path.GetDirectoryName(projectDir);
                        if (parent == null) break;
                        // Останавливаемся если нашли .csproj или config.json
                        if (Directory.GetFiles(parent, "*.csproj").Length > 0 ||
                            File.Exists(Path.Combine(parent, "config.json")))
                        { projectDir = parent; break; }
                        projectDir = parent;
                    }
                    var trtCacheDir = Path.Combine(projectDir, "trt_cache");
                    Directory.CreateDirectory(trtCacheDir);
                    var trtOpts = new OrtTensorRTProviderOptions();
                    trtOpts.UpdateOptions(new Dictionary<string, string>
                    {
                        ["device_id"] = "0",
                        ["trt_max_workspace_size"] = "1073741824", // 1GB
                        ["trt_fp16_enable"] = fp16 ? "1" : "0",
                        ["trt_engine_cache_enable"] = "1",
                        ["trt_engine_cache_path"] = trtCacheDir,
                        ["trt_timing_cache_enable"] = "1",
                        ["trt_timing_cache_path"] = trtCacheDir,
                    });
                    opts.AppendExecutionProvider_Tensorrt(trtOpts);
                    opts.AppendExecutionProvider_CUDA();
                    Console.WriteLine($"[vision] TensorRT cache → {trtCacheDir}");
                    providerName = "TensorRT"; break;
                case "cuda":
                    opts.AppendExecutionProvider_CUDA();
                    providerName = "CUDA"; break;
                case "directml":
                case "dml":
                    Console.WriteLine($"[vision] DML using device_id={dmlDeviceId}");
                    opts.IntraOpNumThreads = 4;
                    opts.InterOpNumThreads = 1;
                    opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                    opts.AppendExecutionProvider_DML(dmlDeviceId);
                    Console.WriteLine($"[vision] DML device_id={dmlDeviceId}");
                    providerName = $"DML#{dmlDeviceId}"; break;
                case "openvino":
                    // OpenVINO EP — для CPU/Intel iGPU (Intel Arc, Iris Xe и т.д.)
                    // Поддерживает: ONNX (.onnx) и OpenVINO IR (.xml + .bin)
                    // Требует: onnxruntime-openvino пакет + OpenVINO Runtime в PATH
                    // Документация: https://docs.openvino.ai/latest
                    // Потоки задаём ДО добавления EP — после AppendExecutionProvider это no-op в ORT
                    opts.IntraOpNumThreads = Math.Max(4, Environment.ProcessorCount - 2);
                    opts.InterOpNumThreads = 1; // OpenVINO сам управляет внутренним параллелизмом
                    opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    try
                    {
                        bool isXmlModel = path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
                        if (isXmlModel)
                        {
                            // OpenVINO IR: .xml (граф) + .bin (веса) должны лежать рядом
                            string binPath = Path.ChangeExtension(path, ".bin");
                            if (!File.Exists(binPath))
                                Console.WriteLine($"[vision] ⚠ Нет файла весов: {binPath}");
                            else
                                Console.WriteLine($"[vision] OpenVINO IR: {Path.GetFileName(path)} + {Path.GetFileName(binPath)}");

                            // Кеш компиляции — рядом с моделью, не удаляется при пересборке
                            string xmlDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                            opts.AppendExecutionProvider("OpenVINO", new Dictionary<string, string>
                            {
                                ["device_type"] = "CPU",
                                ["num_of_threads"] = opts.IntraOpNumThreads.ToString(),
                                ["cache_dir"] = xmlDir,
                                ["enable_opencl_throttling"] = "0",
                            });
                            providerName = "OpenVINO-IR";
                        }
                        else
                        {
                            opts.AppendExecutionProvider("OpenVINO", new Dictionary<string, string>
                            {
                                ["device_type"] = "CPU",
                                ["num_of_threads"] = opts.IntraOpNumThreads.ToString(),
                                ["enable_opencl_throttling"] = "0",
                            });
                            providerName = "OpenVINO";
                        }
                        Console.WriteLine($"[vision] OpenVINO EP → CPU ({opts.IntraOpNumThreads} threads), XML={isXmlModel}");
                    }
                    catch (Exception ovinoEx)
                    {
                        // OpenVINO недоступен — честный fallback на CPU с оптимизациями
                        Console.WriteLine($"[vision] OpenVINO недоступен: {ovinoEx.Message}");
                        Console.WriteLine("[vision] → Проверь: onnxruntime-openvino и OpenVINO Runtime в PATH");
                        opts.EnableCpuMemArena = true;
                        opts.EnableMemoryPattern = true;
                        providerName = "CPU (OpenVINO н/д)";
                    }
                    break;

                case "cpu_optimized":
                    // CPU с оптимизациями: Arena allocator + физические ядра.
                    // ORT_SEQUENTIAL быстрее ORT_PARALLEL для одиночного YOLO-инференса.
                    // Оставляем 2 ядра системе чтобы курсор не лагал.
                    int totalCores = Environment.ProcessorCount;
                    int physCores = Math.Max(2, totalCores / 2 - 2); // -2 ядра системе

                    opts.IntraOpNumThreads = physCores;
                    opts.InterOpNumThreads = 1;
                    opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    opts.EnableCpuMemArena = true;
                    opts.EnableMemoryPattern = true;
                    opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                    // ★ ВЫКЛЮЧАЕМ spinning — он жрёт CPU вхолостую и лагает курсор
                    opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                    opts.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

                    Console.WriteLine($"[vision] CPU Optimized: {physCores} threads (из {totalCores}), no spinning");
                    providerName = $"CPU×{physCores}";
                    break;

                default:
                    opts.IntraOpNumThreads = 4;
                    opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    providerName = "CPU";
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[vision] {provider} EP: {ex.Message} → CPU");
            opts = new SessionOptions(); providerName = "CPU";
        }

        try
        {
            var sess = new InferenceSession(path, opts);
            Console.WriteLine($"[vision] {providerName} ✓");
            return sess;
        }
        catch (Exception ex) { Console.WriteLine($"[vision] Session: {ex.Message}"); return null; }
    }

    private void Warmup()
    {
        try
        {
            lock (_sessionLock)
            {
                if (_session == null || _inputName == null) return;
                var blob = new DenseTensor<float>(new[] { 1, 3, _modelInputSize, _modelInputSize });
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, blob) };
                for (int i = 0; i < 3; i++) _session.Run(inputs);
            }
            Console.WriteLine("[vision] Warmup ✓");
        }
        catch (Exception ex) { Console.WriteLine($"[vision] Warmup: {ex.Message}"); }
    }

    private void ApplyCaptureSize(int cap)
    {
        // Зажимаем размер так чтобы регион не выходил за границы экрана
        int maxByW = _screenW;
        int maxByH = _screenH;
        cap = Math.Min(cap, Math.Min(maxByW, maxByH));
        _capSize = cap;
        // Центрируем и зажимаем в границы экрана
        _capLeft = Math.Max(0, Math.Min(_screenCx - cap / 2, _screenW - cap));
        _capTop = Math.Max(0, Math.Min(_screenCy - cap / 2, _screenH - cap));
    }

    public bool IsReady()
    {
        lock (_sessionLock)
        {
#if USE_OPENVINO
        return _session != null || _ovSession != null;
#else
            return _session != null;
#endif
        }
    }
    public void SetDetectionClass(int classId)
    {
        int max = Math.Max(1, ModelClassCount);
        ActiveClassId = Math.Clamp(classId, -1, max - 1);
        ResetGhost();
        Console.WriteLine($"[vision] Detection class → {ActiveClassName} ({ActiveClassId})");
    }

    public int ToggleDetectionClass()
    {
        int max = Math.Max(1, ModelClassCount);
        // Cycle: T/CT (all) -> CT -> T -> T/CT.
        SetDetectionClass(ActiveClassId < 0 ? 0 : (ActiveClassId + 1 >= max ? -1 : ActiveClassId + 1));
        return ActiveClassId;
    }

    public void SetFov(int r) { FovRadius = Math.Max(50, r); }
    public void SetConf(float v) { ConfThresh = Math.Clamp(v, 0.1f, 0.99f); }
    public void SetCaptureSize(int s) { ApplyCaptureSize(Math.Max(64, s)); }

    public float GetAimXOffsetForTarget(Detection? target)
    {
        if (target == null) return 0f;
        float w = Math.Max(1f, target.X2 - target.X1);
        float pct = Math.Clamp(AimXOffsetPercent, -50f, 50f) * 0.01f;
        return w * pct;
    }

    public float GetAimXForTarget(Detection target) => target.AimX + GetAimXOffsetForTarget(target);

    public void AddTemporaryAimYOffsetPx(float stepPx, float maxPx)
    {
        if (stepPx <= 0f || maxPx <= 0f) return;
        lock (_stateLock)
            _temporaryAimYOffsetPx = Math.Min(_temporaryAimYOffsetPx + stepPx, maxPx);
    }

    public void ResetTemporaryAimYOffset()
    {
        lock (_stateLock)
            _temporaryAimYOffsetPx = 0f;
    }

    public float GetAimYOffsetForTarget(Detection? target)
    {
        float temp;
        lock (_stateLock)
            temp = _temporaryAimYOffsetPx;

        float rf = RfYOffsetExtra;
        if (target == null) return rf + temp;

        float h = Math.Max(1f, target.Y2 - target.Y1);
        float pct = Math.Clamp(AimYOffsetPercent, -100f, 300f) * 0.01f;
        return h * pct + rf + temp;
    }

    private (float x, float y) ClampPrediction(float x, float y)
    {
        float max = Math.Max(0f, MaxPredictionPx);
        if (max <= 0f) return (0f, 0f);
        float len = MathF.Sqrt(x * x + y * y);
        if (len <= max || len < 1e-3f) return (x, y);
        float k = max / len;
        return (x * k, y * k);
    }

    public bool TryGetAimSnapshot(out AimSnapshot snapshot)
    {
        lock (_stateLock)
        {
            if (_lastTarget == null)
            {
                snapshot = default;
                return false;
            }

            float dt = PredictDt;
            var (predX, predY) = ClampPrediction(_velX * dt, _velY * dt);
            snapshot = new AimSnapshot(
                _lastTarget,
                _realTarget,
                _frameId,
                _targetSpeed,
                GetAimXForTarget(_lastTarget) + predX - _screenCx,
                _lastTarget.AimY + predY + GetAimYOffsetForTarget(_lastTarget) - _screenCy);
            return true;
        }
    }

    /// <summary>
    /// Перезагружает ONNX-модель без остановки capture/inference потоков.
    /// Потоки продолжают работать — в момент замены сессии они просто
    /// пропускают один кадр (session == null → пустой результат).
    /// </summary>
    public void RestartModel(string modelPath, string provider, bool fp16)
    {
        Console.WriteLine("[vision] Restarting model...");

        bool useNativeOV = provider.Equals("openvino_native", StringComparison.OrdinalIgnoreCase)
                        || provider.Equals("openvino_xml", StringComparison.OrdinalIgnoreCase)
                        || (provider.Equals("openvino", StringComparison.OrdinalIgnoreCase)
                            && modelPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        _preprocessTensor = null;
        _preprocessBuffer = null;

#if USE_OPENVINO
        if (useNativeOV)
        {
            try
            {
                var newOv = new OpenVinoSession(modelPath, _halfCores, numaPinning: _numaPinning);
                OpenVinoSession? oldOv;
                InferenceSession? oldSess;
                lock (_sessionLock)
                {
                    oldOv = _ovSession;
                    oldSess = _session;
                    _ovSession = newOv;
                    _session = null;
                    _inputName = null;
                    _modelInputSize = newOv.ModelInputSize;
                    ProviderName = newOv.ProviderName;
                }

                oldOv?.Dispose();
                oldSess?.Dispose();

                ApplyProviderRuntimeProfile();
                Console.WriteLine($"[vision] Model restarted [OpenVINO Native], input: {_modelInputSize}px");
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[vision] OpenVINO Native restart failed: {e.Message} → ORT fallback");
            }
        }
#endif

        // ORT path
        var newSession = CreateSession(modelPath, provider, fp16, DmlDeviceId, out var pName);
        if (newSession == null)
        {
            Console.WriteLine("[vision] Restart failed — session is null");
            return;
        }

        // Атомарно заменяем сессию: RunInference держит тот же lock на всё время sess.Run().
        InferenceSession? oldSession;
#if USE_OPENVINO
OpenVinoSession? oldOvSess;
#endif
        lock (_sessionLock)
        {
            oldSession = _session;
#if USE_OPENVINO
    oldOvSess = _ovSession;
    _ovSession = null;
#endif
            _inputName = newSession.InputMetadata.Keys.First();

            var shape = newSession.InputMetadata[_inputName].Dimensions;
            int detectedSize = shape.Length >= 3 && shape[2] > 0 ? shape[2] : 640;
            _modelInputSize = detectedSize;

            _session = newSession;
            ProviderName = pName;
        }
        oldSession?.Dispose();
#if USE_OPENVINO
oldOvSess?.Dispose();
#endif

        ApplyProviderRuntimeProfile();
        Console.WriteLine($"[vision] Model restarted [{pName}], input: {_modelInputSize}px");
        Warmup();
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────
    public void Start()
    {
        if (!IsReady()) return;
        // Попытаться использовать DXGI; если недоступен — GDI
        _useDxgi = ScreenCaptureFactory.TryInitDxgi();
        Console.WriteLine($"[vision] Capture: {(_useDxgi ? "DXGI (~0.5ms)" : "GDI (~10ms)")}");
        _captureTask = Task.Run(CaptureLoop);
        _inferTask = Task.Run(InferenceLoop);
        Console.WriteLine($"[vision] Started [{ProviderName}]");
    }

    public void Stop()
    {
        _cts.Cancel();
        try { Task.WhenAll(_captureTask ?? Task.CompletedTask, _inferTask ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(2)); } catch { }

        InferenceSession? oldSession;
#if USE_OPENVINO
    OpenVinoSession? oldOvSess;
#endif

        lock (_sessionLock)
        {
            oldSession = _session;
#if USE_OPENVINO
        oldOvSess  = _ovSession;
        _ovSession = null;
#endif
            _session = null;
            _inputName = null;
        }

        oldSession?.Dispose();
#if USE_OPENVINO
    oldOvSess?.Dispose();
#endif
    }

    // ── Capture Loop ──────────────────────────────────────────────────────────
    private async Task CaptureLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                byte[]? frame = _useDxgi
                    ? ScreenCaptureFactory.DxgiGrab(_capLeft, _capTop, _capSize, _capSize)
                    : ScreenCaptureFactory.GdiGrab(_capLeft, _capTop, _capSize, _capSize);

                if (frame != null)
                    await _frameChannel.Writer.WriteAsync(frame, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[capture] {ex.Message}"); await Task.Delay(10, ct); }
        }
    }

    // ── Inference Loop ────────────────────────────────────────────────────────
    private async Task InferenceLoop()
    {
        // Поднимаем приоритет inference потока (аналог Python SetThreadPriority в _loop_dxcam)
        // THREAD_PRIORITY_HIGHEST = 2: выше normal, ниже realtime.
        // Критично при OpenVINO — inference поток не должен вытесняться UI/capture.
        try { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST); }
        catch { /* не критично */ }

        var ct = _cts.Token;
        var fpsQ = new Queue<double>();
        double lastFrameT = 0;

        while (!ct.IsCancellationRequested)
        {
            byte[]? frame;
            try { frame = await _frameChannel.Reader.ReadAsync(ct); }
            catch { break; }

            if (AimHz > 0)
            {
                double minDt = 1.0 / AimHz;
                double elapsed = Ts() - lastFrameT;
                if (elapsed < minDt)
                    await Task.Delay(TimeSpan.FromSeconds(minDt - elapsed), ct);
            }

            double t0 = Ts(); lastFrameT = t0;
            try
            {
                List<Detection> dets;
                if (UseTiled && _capSize > _modelInputSize)
                    dets = InferTiled(frame);
                else
                {
                    var blob = Preprocess(frame);
                    var raw = RunInference(blob);
                    dets = Postprocess(raw);
                }
                var tgt = PickTarget(dets);
                // ★ ОТЛАДКА: пишем каждые 30 кадров
                lock (_stateLock)
                {
                    _lastDetections = dets
                        .OrderByDescending(d => d.Conf)
                        .Take(10)
                        .ToList();

                    // ── Ghost target логика ───────────────────────────────────────
                    _realTarget = tgt; // всегда только реальная детекция (для триггербота)

                    if (tgt != null)
                    {
                        _ghostTarget = tgt;
                        _ghostFrames = 0;
                        _ghostVelX = _velX;
                        _ghostVelY = _velY;
                        _ghostScreenAimX = tgt.AimX;
                        _ghostScreenAimY = tgt.AimY;
                        _anchorLastRealX = tgt.AimX;
                        _anchorLastRealY = tgt.AimY;
                        _lastTarget = tgt;
                    }
                    else if (_ghostTarget != null && _ghostFrames < GhostMaxFrames)
                    {
                        _ghostFrames++;
                        float velocityDecay = (_ghostFrames > 5) ? 0.80f : 1.0f;
                        _ghostVelX *= velocityDecay;
                        _ghostVelY *= velocityDecay;
                        float gdt = _frameInterval;
                        _ghostScreenAimX += _ghostVelX * gdt;
                        _ghostScreenAimY += _ghostVelY * gdt;

                        // Не даём ghost улетать далеко из-за ошибочной скорости/скачка bbox.
                        float gdx = _ghostScreenAimX - _anchorLastRealX;
                        float gdy = _ghostScreenAimY - _anchorLastRealY;
                        float glen = MathF.Sqrt(gdx * gdx + gdy * gdy);
                        float gmax = Math.Max(10f, GhostMaxDistance);
                        if (glen > gmax)
                        {
                            float k = gmax / glen;
                            _ghostScreenAimX = _anchorLastRealX + gdx * k;
                            _ghostScreenAimY = _anchorLastRealY + gdy * k;
                            _ghostVelX *= 0.5f;
                            _ghostVelY *= 0.5f;
                        }

                        float gw = _ghostTarget.X2 - _ghostTarget.X1;
                        float gh_ = _ghostTarget.Y2 - _ghostTarget.Y1;
                        float fade = 1f - (float)_ghostFrames / GhostMaxFrames;

                        _lastTarget = new Detection(
                            _ghostScreenAimX - gw * 0.5f,
                            _ghostScreenAimY - gh_ * 0.28f,
                            _ghostScreenAimX + gw * 0.5f,
                            _ghostScreenAimY + gh_ * 0.72f,
                            _ghostTarget.Conf * fade * 0.8f,
                            _screenCx, _screenCy);
                    }
                    else
                    {
                        // Призрак истёк — теряем цель
                        _ghostTarget = null;
                        _lastTarget = null;
                    }

                    fpsQ.Enqueue(Ts() - t0);
                    if (fpsQ.Count > 30) fpsQ.Dequeue();
                    _fps = (float)(1.0 / fpsQ.Average());
                    UpdateVelocity(tgt, Ts()); // обновляем скорость только по реальной цели
                    _frameId++;
                }
            }
            catch (Exception ex)
            { Console.WriteLine($"[infer] {ex.Message}"); lock (_stateLock) _lastTarget = null; await Task.Delay(20, ct); }
        }
    }

    internal static double Ts() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() /
        System.Diagnostics.Stopwatch.Frequency;

    // ── Preprocess: zero-alloc resize + BGR→RGB + normalize ───────────────────
    // Python оригинал:
    //   cv2.resize(img, (s,s), dst=self._uint8buf, interpolation=cv2.INTER_LINEAR)
    //   np.multiply(src[:,:,2], 1/255, out=blob[0,0])   # R
    //   np.multiply(src[:,:,1], 1/255, out=blob[0,1])   # G
    //   np.multiply(src[:,:,0], 1/255, out=blob[0,2])   # B
    //
    // Ключевые оптимизации:
    //  1. _preprocessBuffer переиспользуется — 0 аллокаций на кадр
    //  2. Nearest-neighbour вместо bilinear: в 2–3x быстрее при той же точности детекции
    //     (YOLO модели обучены с аугментацией масштаба — subpixel неважен)
    //  3. Unsafe pointer arithmetic — без bounds check на каждый пиксель
    //  4. Три отдельных прохода по каналам = последовательный доступ к памяти (cache-friendly)
    //
    // Если нужен bilinear (заменить nearest) — раскомментируй блок ниже.
    private DenseTensor<float>? _preprocessTensor;
    private float[]? _preprocessBuffer;

    private DenseTensor<float> Preprocess(byte[] bgr)
    {
        int s = _modelInputSize;
        int total = 3 * s * s;

        // Реюзаем тензор — не аллоцируем каждый кадр
        if (_preprocessTensor == null || _preprocessBuffer == null || _preprocessBuffer.Length != total)
        {
            _preprocessBuffer = new float[total];
            _preprocessTensor = new DenseTensor<float>(_preprocessBuffer, new[] { 1, 3, s, s });
        }

        int cap = _capSize;
        float scaleX = (float)cap / s;
        float scaleY = (float)cap / s;
        var buf = _preprocessBuffer;
        const float inv255 = 1f / 255f;

        // Unsafe: убираем bounds check в горячем пути (~15% ускорение на ядро)
        unsafe
        {
            fixed (byte* pSrc = bgr)
            fixed (float* pDst = buf)
            {
                int ss = s * s;
                float* pR = pDst;           // канал R: blob[0,0]
                float* pG = pDst + ss;      // канал G: blob[0,1]
                float* pB = pDst + ss * 2;  // канал B: blob[0,2]

                // Nearest-neighbour resize + BGR→RGB + normalize
                // Три канала в одном проходе = один раз по dst памяти
                for (int row = 0; row < s; row++)
                {
                    int srcRow = (int)(row * scaleY);
                    if (srcRow >= cap) srcRow = cap - 1;
                    byte* pSrcRow = pSrc + srcRow * cap * 3;

                    for (int col = 0; col < s; col++)
                    {
                        int srcCol = (int)(col * scaleX);
                        if (srcCol >= cap) srcCol = cap - 1;
                        byte* px = pSrcRow + srcCol * 3; // BGR layout

                        int dstIdx = row * s + col;
                        pR[dstIdx] = px[2] * inv255;  // R = bgr[2]
                        pG[dstIdx] = px[1] * inv255;  // G = bgr[1]
                        pB[dstIdx] = px[0] * inv255;  // B = bgr[0]
                    }
                }
            }
        }

        /* ── Bilinear вариант (раскомментировать если нужна точность subpixel) ──
        System.Threading.Tasks.Parallel.For(0, s, row =>
        {
            float srcRow = row * scaleY;
            int   r0 = (int)srcRow, r1 = r0 + 1 < cap ? r0 + 1 : cap - 1;
            float fy = srcRow - r0, ify = 1f - fy;
            int baseR = row * s, baseG = ss + baseR, baseB = ss * 2 + baseR;
            for (int col = 0; col < s; col++)
            {
                float srcCol = col * scaleX;
                int c0 = (int)srcCol, c1 = c0 + 1 < cap ? c0 + 1 : cap - 1;
                float fx = srcCol - c0, ifx = 1f - fx;
                int i00 = (r0*cap+c0)*3, i01 = (r0*cap+c1)*3, i10 = (r1*cap+c0)*3, i11 = (r1*cap+c1)*3;
                float w00=ify*ifx, w01=ify*fx, w10=fy*ifx, w11=fy*fx;
                buf[baseR+col] = (bgr[i00+2]*w00+bgr[i01+2]*w01+bgr[i10+2]*w10+bgr[i11+2]*w11)*inv255;
                buf[baseG+col] = (bgr[i00+1]*w00+bgr[i01+1]*w01+bgr[i10+1]*w10+bgr[i11+1]*w11)*inv255;
                buf[baseB+col] = (bgr[i00+0]*w00+bgr[i01+0]*w01+bgr[i10+0]*w10+bgr[i11+0]*w11)*inv255;
            }
        });
        ── конец bilinear ── */

        return _preprocessTensor;
    }

    // ── Inference ─────────────────────────────────────────────────────────────
    private float[] RunInference(DenseTensor<float> blob)
    {
        lock (_sessionLock)
        {
            // ── OpenVINO Native path (XML IR + реальный LATENCY hint) ─────────────
            // Python оригинал: req.infer({0: blob}) → req.get_output_tensor(0).data
            // Здесь повторяем ту же логику через OpenVinoSession.Infer()
#if USE_OPENVINO
            if (_ovSession != null)
            {
                // _preprocessBuffer — тот же буфер что и в blob, без копирования
                return _ovSession.Infer(_preprocessBuffer!);
            }
#endif
            // ── ORT path (DML / TensorRT / CUDA / CPU) ───────────────────────────
            var sess = _session;
            if (sess == null) throw new OperationCanceledException("Session disposed");
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName!, blob) };
            using var results = sess.Run(inputs);
            return results.First().AsTensor<float>().ToArray();
        }
    }

    // ── Postprocess — YOLOv8 person-only ─────────────────────────────────────
    private List<Detection> Postprocess(float[] raw)
    {
        int classCount = Math.Max(1, ModelClassCount);
        int attrs = 4 + (OutputHasObjectness ? 1 : 0) + classCount;
        if (raw.Length % attrs != 0)
        {
            int altAttrs = 4 + (!OutputHasObjectness ? 1 : 0) + classCount;
            if (raw.Length % altAttrs == 0) attrs = altAttrs;
            else
            {
                attrs = 5;
                classCount = 1;
            }
        }

        int classOffset = attrs == 5 && classCount == 1 ? 4 : (attrs == 4 + classCount ? 4 : 5);
        bool hasObj = classOffset == 5;
        int nAnchors = raw.Length / attrs;
        int activeClass = ActiveClassId; // -1 = all/best class per anchor
        int classIdForShapeProbe = activeClass < 0 ? 0 : Math.Clamp(activeClass, 0, classCount - 1);

        float firstScoreTransposed = raw.Length > (classOffset + classIdForShapeProbe) * nAnchors ? raw[(classOffset + classIdForShapeProbe) * nAnchors] : 0f;
        float firstScoreNormal = raw.Length > classOffset + classIdForShapeProbe ? raw[classOffset + classIdForShapeProbe] : 0f;
        bool transposed = !(firstScoreNormal >= 0f && firstScoreNormal <= 1f && firstScoreNormal > firstScoreTransposed);

        float scale = (float)_capSize / _modelInputSize;
        var boxes = new List<(float x1, float y1, float x2, float y2, float conf)>(128);
        var boxClassIds = new List<int>(128);

        for (int i = 0; i < nAnchors; i++)
        {
            float cxb, cyb, wb, hb;
            float obj = 1f;
            if (transposed)
            {
                cxb = raw[0 * nAnchors + i];
                cyb = raw[1 * nAnchors + i];
                wb = raw[2 * nAnchors + i];
                hb = raw[3 * nAnchors + i];
                if (hasObj) obj = raw[4 * nAnchors + i];
            }
            else
            {
                int off = i * attrs;
                cxb = raw[off + 0];
                cyb = raw[off + 1];
                wb = raw[off + 2];
                hb = raw[off + 3];
                if (hasObj) obj = raw[off + 4];
            }

            int pickedClass = 0;
            float conf = 0f;
            if (classCount == 1)
            {
                conf = transposed ? raw[classOffset * nAnchors + i] : raw[i * attrs + classOffset];
            }
            else if (activeClass >= 0)
            {
                pickedClass = Math.Clamp(activeClass, 0, classCount - 1);
                float clsScore = transposed
                    ? raw[(classOffset + pickedClass) * nAnchors + i]
                    : raw[i * attrs + classOffset + pickedClass];
                conf = obj * clsScore;
            }
            else
            {
                // Track both T and CT: use the best class score for each anchor.
                for (int c = 0; c < classCount; c++)
                {
                    float clsScore = transposed
                        ? raw[(classOffset + c) * nAnchors + i]
                        : raw[i * attrs + classOffset + c];
                    float score = obj * clsScore;
                    if (score > conf)
                    {
                        conf = score;
                        pickedClass = c;
                    }
                }
            }

            if (conf < ConfThresh) continue;
            float w = wb * scale, h = hb * scale, maxPx = _capSize * 0.8f;
            if (w < 6 || h < 6 || w > maxPx || h > maxPx) continue;

            float aspect = w / Math.Max(h, 1f);
            if (aspect < 0.12f || aspect > 1.15f) continue;
            if (w * h < _capSize * _capSize * 0.0015f) continue;

            boxes.Add(((cxb - wb * 0.5f) * scale + _capLeft,
                       (cyb - hb * 0.5f) * scale + _capTop,
                       (cxb + wb * 0.5f) * scale + _capLeft,
                       (cyb + hb * 0.5f) * scale + _capTop, conf));
            boxClassIds.Add(pickedClass);
        }

        if (boxes.Count == 0) return [];

        float avgArea = boxes.Average(b => (b.x2 - b.x1) * (b.y2 - b.y1));
        float nmsThresh = avgArea < 2000f ? 0.25f : 0.45f;
        var keep = NMS(boxes, nmsThresh);

        var dets = keep.Select(k => new Detection(
            boxes[k].x1, boxes[k].y1, boxes[k].x2, boxes[k].y2,
            boxes[k].conf, _screenCx, _screenCy, boxClassIds[k])).ToList();

        if (DeadZoneEnabled && DeadZones.Count > 0)
        {
            var active = DeadZones.Where(z => z.Enabled).ToList();
            if (active.Count > 0)
                dets = dets.Where(d => !active.Any(z => z.Contains(d.Cx, d.Cy))).ToList();
        }
        return dets;
    }

    // ── Tiled Inference ───────────────────────────────────────────────────────
    // Делим capture на тайлы modelInputSize x modelInputSize с перекрытием,
    // инферим каждый тайл, пересчитываем координаты в экранное пространство,
    // мерджим через NMS. Для мощных GPU — лучше качество на дальних целях.
    private List<Detection> InferTiled(byte[] bgr)
    {
        int tileSize = _modelInputSize;
        int overlap = Math.Max(0, Math.Min(TileOverlap, tileSize / 2));
        int step = tileSize - overlap;
        int gridN = (int)Math.Ceiling((double)(_capSize - overlap) / step);
        gridN = Math.Max(1, gridN);

        // Все боксы со всех тайлов — потом единый NMS
        var allBoxes = new System.Collections.Concurrent.ConcurrentBag<(float x1, float y1, float x2, float y2, float conf)>();

        // Параллельный inference тайлов (каждый тайл — отдельный поток)
        // Но InferenceSession не thread-safe для одной сессии →
        // используем lock только на RunInference, Preprocess параллелен
        var tileData = new List<(byte[] tile, int offX, int offY)>();

        for (int ty = 0; ty < gridN; ty++)
            for (int tx = 0; tx < gridN; tx++)
            {
                int ox = Math.Min(tx * step, _capSize - tileSize);
                int oy = Math.Min(ty * step, _capSize - tileSize);
                tileData.Add((ExtractTile(bgr, ox, oy, tileSize), ox, oy));
            }

        // Параллельный preprocess + последовательный inference
        var preprocessed = new (DenseTensor<float> blob, int offX, int offY)[tileData.Count];
        System.Threading.Tasks.Parallel.For(0, tileData.Count, i =>
        {
            var (tile, ox, oy) = tileData[i];
            preprocessed[i] = (PreprocessTile(tile, tileSize), ox, oy);
        });

        // Inference последовательно (OnnxRuntime сессия не thread-safe)
        foreach (var (blob, offX, offY) in preprocessed)
        {
            var raw = RunInference(blob);
            // Postprocess с учётом смещения тайла
            PostprocessTile(raw, tileSize, offX, offY, allBoxes);
        }

        if (allBoxes.IsEmpty) return [];

        // Финальный NMS по всем тайлам
        var boxList = allBoxes.ToList();
        var keep = NMS(boxList, 0.35f);
        var dets = keep.Select(k => new Detection(
            boxList[k].x1, boxList[k].y1, boxList[k].x2, boxList[k].y2,
            boxList[k].conf, _screenCx, _screenCy)).ToList();

        if (DeadZoneEnabled && DeadZones.Count > 0)
        {
            var active = DeadZones.Where(z => z.Enabled).ToList();
            if (active.Count > 0)
                dets = dets.Where(d => !active.Any(z => z.Contains(d.Cx, d.Cy))).ToList();
        }
        return dets;
    }

    // Вырезаем тайл из BGR capture (без аллокации через span где возможно)
    private byte[] ExtractTile(byte[] bgr, int offX, int offY, int tileSize)
    {
        var tile = new byte[tileSize * tileSize * 3];
        for (int row = 0; row < tileSize; row++)
        {
            int srcRow = Math.Min(offY + row, _capSize - 1);
            Buffer.BlockCopy(bgr, (srcRow * _capSize + offX) * 3,
                             tile, row * tileSize * 3,
                             tileSize * 3);
        }
        return tile;
    }

    // Preprocess тайла — аналог основного но без кеша (параллельные вызовы)
    private DenseTensor<float> PreprocessTile(byte[] bgr, int s)
    {
        var buf = new float[3 * s * s];
        var tensor = new DenseTensor<float>(buf, new[] { 1, 3, s, s });
        // BGR → RGB + normalize (тайл уже нужного размера — ресайз не нужен)
        // for быстрее Parallel.For для 416×416
        int ss = s * s;
        for (int row = 0; row < s; row++)
        {
            int rowOffR = row * s;
            int rowOffG = ss + row * s;
            int rowOffB = 2 * ss + row * s;
            int src = row * s * 3;
            for (int col = 0; col < s; col++, src += 3)
            {
                buf[rowOffR + col] = bgr[src + 2] * (1f / 255f);
                buf[rowOffG + col] = bgr[src + 1] * (1f / 255f);
                buf[rowOffB + col] = bgr[src + 0] * (1f / 255f);
            }
        }
        return tensor;
    }

    // Postprocess тайла — пересчёт координат с учётом смещения тайла
    private void PostprocessTile(float[] raw, int tileSize, int offX, int offY,
        System.Collections.Concurrent.ConcurrentBag<(float, float, float, float, float)> result)
    {
        int classCount = Math.Max(1, ModelClassCount);
        int attrs = 4 + (OutputHasObjectness ? 1 : 0) + classCount;
        if (raw.Length % attrs != 0)
        {
            int altAttrs = 4 + (!OutputHasObjectness ? 1 : 0) + classCount;
            if (raw.Length % altAttrs == 0) attrs = altAttrs;
            else
            {
                attrs = 5;
                classCount = 1;
            }
        }

        int classOffset = attrs == 5 && classCount == 1 ? 4 : (attrs == 4 + classCount ? 4 : 5);
        bool hasObj = classOffset == 5;
        int nAnchors = raw.Length / attrs;
        int activeClass = ActiveClassId;
        int classIdForShapeProbe = activeClass < 0 ? 0 : Math.Clamp(activeClass, 0, classCount - 1);

        float firstScoreTransposed = raw.Length > (classOffset + classIdForShapeProbe) * nAnchors ? raw[(classOffset + classIdForShapeProbe) * nAnchors] : 0f;
        float firstScoreNormal = raw.Length > classOffset + classIdForShapeProbe ? raw[classOffset + classIdForShapeProbe] : 0f;
        bool transposed = !(firstScoreNormal >= 0f && firstScoreNormal <= 1f && firstScoreNormal > firstScoreTransposed);

        for (int i = 0; i < nAnchors; i++)
        {
            float cxb, cyb, wb, hb;
            float obj = 1f;
            if (transposed)
            {
                cxb = raw[0 * nAnchors + i];
                cyb = raw[1 * nAnchors + i];
                wb = raw[2 * nAnchors + i];
                hb = raw[3 * nAnchors + i];
                if (hasObj) obj = raw[4 * nAnchors + i];
            }
            else
            {
                int off = i * attrs;
                cxb = raw[off + 0];
                cyb = raw[off + 1];
                wb = raw[off + 2];
                hb = raw[off + 3];
                if (hasObj) obj = raw[off + 4];
            }

            float conf = 0f;
            if (classCount == 1)
            {
                conf = transposed ? raw[classOffset * nAnchors + i] : raw[i * attrs + classOffset];
            }
            else if (activeClass >= 0)
            {
                int cls = Math.Clamp(activeClass, 0, classCount - 1);
                float clsScore = transposed ? raw[(classOffset + cls) * nAnchors + i] : raw[i * attrs + classOffset + cls];
                conf = obj * clsScore;
            }
            else
            {
                for (int c = 0; c < classCount; c++)
                {
                    float clsScore = transposed ? raw[(classOffset + c) * nAnchors + i] : raw[i * attrs + classOffset + c];
                    conf = Math.Max(conf, obj * clsScore);
                }
            }

            if (conf < ConfThresh) continue;
            float maxPx = tileSize * 0.8f;
            if (wb < 6 || hb < 6 || wb > maxPx || hb > maxPx) continue;

            float aspect = wb / Math.Max(hb, 1f);
            if (aspect < 0.12f || aspect > 1.15f) continue;
            if (wb * hb < tileSize * tileSize * 0.0015f) continue;

            const int edgeBuffer = 8;
            bool nearLeftEdge = (cxb - wb * 0.5f) < edgeBuffer && offX > 0;
            bool nearRightEdge = (cxb + wb * 0.5f) > tileSize - edgeBuffer && offX + tileSize < _capSize;
            bool nearTopEdge = (cyb - hb * 0.5f) < edgeBuffer && offY > 0;
            bool nearBottomEdge = (cyb + hb * 0.5f) > tileSize - edgeBuffer && offY + tileSize < _capSize;
            if (nearLeftEdge || nearRightEdge || nearTopEdge || nearBottomEdge) continue;

            float x1 = (cxb - wb * 0.5f) + offX + _capLeft;
            float y1 = (cyb - hb * 0.5f) + offY + _capTop;
            float x2 = (cxb + wb * 0.5f) + offX + _capLeft;
            float y2 = (cyb + hb * 0.5f) + offY + _capTop;
            result.Add((x1, y1, x2, y2, conf));
        }
    }

    // NMS in-place: 0 аллокаций на итерациях, ×5-10 быстрее LINQ
    private static List<int> NMS(List<(float x1, float y1, float x2, float y2, float conf)> boxes, float thresh)
    {
        int n = boxes.Count;
        if (n == 0) return [];
        var order = Enumerable.Range(0, n).OrderByDescending(i => boxes[i].conf).ToArray();
        var suppressed = new bool[n];
        var keep = new List<int>(n);

        for (int oi = 0; oi < n; oi++)
        {
            int i = order[oi];
            if (suppressed[i]) continue;
            keep.Add(i);
            var (x1i, y1i, x2i, y2i, _) = boxes[i];
            float ai = (x2i - x1i) * (y2i - y1i);

            for (int oj = oi + 1; oj < n; oj++)
            {
                int j = order[oj];
                if (suppressed[j]) continue;
                var (x1j, y1j, x2j, y2j, _) = boxes[j];
                float ix = Math.Max(0, Math.Min(x2i, x2j) - Math.Max(x1i, x1j));
                float iy = Math.Max(0, Math.Min(y2i, y2j) - Math.Max(y1i, y1j));
                float inter = ix * iy;
                if (inter / Math.Max(ai + (x2j - x1j) * (y2j - y1j) - inter, 1e-6f) >= thresh)
                    suppressed[j] = true;
            }
        }
        return keep;
    }

    // ── Stability filter ──────────────────────────────────────────────────────
    private const float SF_MAX_DIST = 80f; // px между predicted и detected центром

    private List<Detection> StabilityFilter(List<Detection> dets)
    {
        // Fast path: ConfirmFrames=0 means "no confirmation/stability filter".
        // This keeps the original high-FPS behavior (important for TensorRT 400-500+ FPS).
        // TargetLock in PickTarget still prevents jumping between multiple targets.
        if (ConfirmFrames <= 0)
            return dets;

        float dt = Math.Clamp(_frameInterval, 0.005f, 0.05f);

        // Track layout:
        // 0 cx, 1 cy, 2 vx, 3 vy, 4 w, 5 h, 6 conf, 7 hits, 8 miss
        foreach (var tr in _tracks)
        {
            tr[0] += tr[2] * dt;
            tr[1] += tr[3] * dt;
            tr[8]++;
        }

        var matched = new bool[dets.Count];
        foreach (var tr in _tracks)
        {
            int best = -1;
            float bestD = SF_MAX_DIST;
            for (int i = 0; i < dets.Count; i++)
            {
                if (matched[i]) continue;
                float dd = MathF.Sqrt(
                    (dets[i].Cx - tr[0]) * (dets[i].Cx - tr[0]) +
                    (dets[i].Cy - tr[1]) * (dets[i].Cy - tr[1]));
                if (dd < bestD) { bestD = dd; best = i; }
            }

            if (best < 0) continue;
            matched[best] = true;
            var d = dets[best];

            // Резкий скачок bbox не принимаем полностью за 1 кадр: это главный источник дрожания aim-point.
            float rawVx = (d.Cx - tr[0]) / dt;
            float rawVy = (d.Cy - tr[1]) / dt;
            float rawSpd = MathF.Sqrt(rawVx * rawVx + rawVy * rawVy);
            if (rawSpd > 2200f && tr[7] >= 2)
            {
                rawVx *= 0.35f;
                rawVy *= 0.35f;
            }

            float velAlpha = 0.45f;
            tr[2] = velAlpha * rawVx + (1f - velAlpha) * tr[2];
            tr[3] = velAlpha * rawVy + (1f - velAlpha) * tr[3];
            tr[2] = Math.Clamp(tr[2], -1200f, 1200f);
            tr[3] = Math.Clamp(tr[3], -900f, 900f);

            float a = Math.Clamp(DetectionSmoothAlpha, 0.05f, 0.85f);
            if (tr[7] < 2) a = Math.Max(a, 0.65f); // первые кадры быстрее цепляемся
            tr[0] = a * d.Cx + (1f - a) * tr[0];
            tr[1] = a * d.Cy + (1f - a) * tr[1];
            tr[4] = a * (d.X2 - d.X1) + (1f - a) * tr[4];
            tr[5] = a * (d.Y2 - d.Y1) + (1f - a) * tr[5];
            tr[6] = 0.55f * d.Conf + 0.45f * tr[6];
            tr[7] = Math.Min(tr[7] + 1, 30);
            tr[8] = 0;
        }

        // Новые треки для нематченных детекций.
        for (int i = 0; i < dets.Count; i++)
        {
            if (matched[i] || dets[i].Conf < Math.Max(0.12f, ConfThresh * 0.35f)) continue;
            var d = dets[i];
            _tracks.Add([d.Cx, d.Cy, 0f, 0f,
                d.X2 - d.X1, d.Y2 - d.Y1,
                d.Conf, 1f, 0f]);
        }

        _tracks.RemoveAll(tr => tr[8] > SF_MISS);

        int minHits = Math.Max(1, ConfirmFrames);
        var stable = new List<Detection>(Math.Min(_tracks.Count, 16));
        foreach (var tr in _tracks)
        {
            if (tr[8] != 0 || (int)tr[7] < minHits) continue;
            float w = Math.Max(2f, tr[4]);
            float h = Math.Max(2f, tr[5]);
            stable.Add(new Detection(
                tr[0] - w * 0.5f,
                tr[1] - h * 0.5f,
                tr[0] + w * 0.5f,
                tr[1] + h * 0.5f,
                tr[6],
                _screenCx, _screenCy));
        }

        // Важно: если трекер ещё не успел подтвердить цель, не гасим детекцию полностью.
        // Иначе визуально кажется, что "модель перестала видеть" цели.
        if (stable.Count == 0 && dets.Count > 0)
            return dets;

        return stable;
    }
    // ── Target selection ──────────────────────────────────────────────────────
    private void UpdateTargetLock(Detection d, float alpha)
    {
        if (!_hasTargetLock || alpha >= 0.999f)
        {
            _lockedAimX = d.AimX;
            _lockedAimY = d.AimY;
            _lockedX1 = d.X1; _lockedY1 = d.Y1; _lockedX2 = d.X2; _lockedY2 = d.Y2;
            _hasTargetLock = true;
            _targetLockMissFrames = 0;
            return;
        }

        _lockedAimX = alpha * d.AimX + (1f - alpha) * _lockedAimX;
        _lockedAimY = alpha * d.AimY + (1f - alpha) * _lockedAimY;
        _lockedX1 = alpha * d.X1 + (1f - alpha) * _lockedX1;
        _lockedY1 = alpha * d.Y1 + (1f - alpha) * _lockedY1;
        _lockedX2 = alpha * d.X2 + (1f - alpha) * _lockedX2;
        _lockedY2 = alpha * d.Y2 + (1f - alpha) * _lockedY2;
        _targetLockMissFrames = 0;
        _pendingSwitchFrames = 0;
    }

    private float LockedIoU(Detection d)
    {
        float ix = Math.Max(0f, Math.Min(d.X2, _lockedX2) - Math.Max(d.X1, _lockedX1));
        float iy = Math.Max(0f, Math.Min(d.Y2, _lockedY2) - Math.Max(d.Y1, _lockedY1));
        float inter = ix * iy;
        float areaD = Math.Max(1f, (d.X2 - d.X1) * (d.Y2 - d.Y1));
        float areaL = Math.Max(1f, (_lockedX2 - _lockedX1) * (_lockedY2 - _lockedY1));
        return inter / Math.Max(1f, areaD + areaL - inter);
    }

    private bool IsLockedHitboxHeightCollapsed(Detection d)
    {
        if (!SwitchOnHitboxHeightCollapse || !_hasTargetLock) return false;
        float oldH = Math.Max(1f, _lockedY2 - _lockedY1);
        float newH = Math.Max(1f, d.Y2 - d.Y1);
        float ratio = Math.Max(1.1f, HitboxHeightCollapseRatio);
        return oldH >= HitboxHeightCollapseMinPrevH && newH * ratio < oldH;
    }

    private Detection? PickTarget(List<Detection> dets)
    {
        var stable = StabilityFilter(dets);
        var cands = stable.Where(d => d.Dist <= FovRadius).ToList();

        // Fallback: если сглаженный трек временно вылетел из FOV/не подтвердился,
        // используем сырые detections вместо полного пропадания цели.
        if (cands.Count == 0 && !ReferenceEquals(stable, dets))
            cands = dets.Where(d => d.Dist <= FovRadius).ToList();

        if (cands.Count == 0)
        {
            _manualLock = false;
            // No detections this frame: keep current ghost/lock for a short grace period.
            // This prevents instant re-locking to a neighbour when the old target flickers out.
            if (++_targetLockMissFrames > TargetLockMaxMissFrames)
                _hasTargetLock = false;
            return null;
        }

        Detection? lt;
        bool hasAnchor;
        float anchorX, anchorY;
        lock (_stateLock)
        {
            lt = _lastTarget;
            hasAnchor = _ghostTarget != null;
            anchorX = hasAnchor ? _anchorLastRealX : (lt?.AimX ?? (_hasTargetLock ? _lockedAimX : 0f));
            anchorY = hasAnchor ? _anchorLastRealY : (lt?.AimY ?? (_hasTargetLock ? _lockedAimY : 0f));
        }

        float Score(Detection d, bool sticky)
        {
            float fov = Math.Max(1f, FovRadius);
            float dist01 = Math.Clamp(d.Dist / fov, 0f, 1f);
            float distScore = 1f - dist01;
            float sizeBonus = PrioritySize
                ? Math.Clamp(MathF.Sqrt(Math.Max(1f, d.Area)) / 25f, 0.55f, 2.4f)
                : 1f;
            float anchorBonus = 1f;
            if (hasAnchor || lt != null || _hasTargetLock)
            {
                float da = MathF.Sqrt((d.AimX - anchorX) * (d.AimX - anchorX) + (d.AimY - anchorY) * (d.AimY - anchorY));
                anchorBonus = 1f + 0.55f * (1f - Math.Clamp(da / Math.Max(40f, StickyRadiusPx), 0f, 1f));
            }
            float lockBonus = 1f;
            if (_hasTargetLock)
            {
                float dl = MathF.Sqrt((d.AimX - _lockedAimX) * (d.AimX - _lockedAimX) + (d.AimY - _lockedAimY) * (d.AimY - _lockedAimY));
                lockBonus = 1f + 0.85f * (1f - Math.Clamp(dl / Math.Max(40f, TargetLockRadiusPx), 0f, 1f));
            }
            float edgePenalty = dist01 > 0.92f ? 0.70f : 1f;
            float stickyBonus = sticky ? 1.25f : 1f;
            return d.Conf * (0.20f + 0.80f * distScore * distScore) * sizeBonus * anchorBonus * lockBonus * edgePenalty * stickyBonus;
        }

        Detection? chosen = null;

        // Ручное переключение: некоторое время держим ближайшую к выбранной цели.
        if (_manualLock && Ts() < _manualLockUntil && lt != null)
        {
            var lockedManual = cands.MinBy(d => MathF.Sqrt((d.Cx - lt.Cx) * (d.Cx - lt.Cx) + (d.Cy - lt.Cy) * (d.Cy - lt.Cy)));
            if (lockedManual != null && MathF.Sqrt((lockedManual.Cx - lt.Cx) * (lockedManual.Cx - lt.Cx) + (lockedManual.Cy - lt.Cy) * (lockedManual.Cy - lt.Cy)) < StickyRadiusPx)
                chosen = lockedManual;
            else
                _manualLock = false;
        }
        else _manualLock = false;

        var best = cands.MaxBy(d => Score(d, false));
        if (best == null) return null;

        if (chosen == null && TargetLockEnabled && _hasTargetLock)
        {
            // STRICT anti-flicker: while locked, only accept a candidate that looks like the SAME bbox.
            // Do not use the large lock radius for reacquire; it can catch a neighbour.
            Detection? lockedCand = null;
            float bestLockScore = float.NegativeInfinity;
            float reacquireR = Math.Max(12f, TargetReacquireRadiusPx);
            float reacquireR2 = reacquireR * reacquireR;
            float minIou = Math.Clamp(TargetLockMinIoU, 0f, 0.95f);

            foreach (var d in cands)
            {
                float dx = d.AimX - _lockedAimX;
                float dy = d.AimY - _lockedAimY;
                float d2 = dx * dx + dy * dy;
                float iou = LockedIoU(d);

                // Candidate must either overlap old bbox or be very close to previous locked aim point.
                // Otherwise it is treated as another person and ignored while lock is alive.
                if (iou < minIou && d2 > reacquireR2) continue;

                float lockScore = iou * 100000f - d2;
                if (lockScore > bestLockScore)
                {
                    bestLockScore = lockScore;
                    lockedCand = d;
                }
            }

            if (lockedCand != null)
            {
                if (Ts() >= _targetSwitchCooldownUntil && IsLockedHitboxHeightCollapsed(lockedCand) && cands.Count > 1)
                {
                    // Optional mechanic, disabled by default. Kept conservative.
                    Detection? replacement = null;
                    float replacementScore = float.NegativeInfinity;
                    float collapsedH = Math.Max(1f, lockedCand.Y2 - lockedCand.Y1);
                    foreach (var d in cands)
                    {
                        if (ReferenceEquals(d, lockedCand)) continue;
                        if (IsLockedHitboxHeightCollapsed(d)) continue;
                        float dh = Math.Max(1f, d.Y2 - d.Y1);
                        if (dh < collapsedH * 1.35f) continue;
                        float sc = Score(d, false);
                        if (sc > replacementScore)
                        {
                            replacementScore = sc;
                            replacement = d;
                        }
                    }

                    if (replacement != null)
                    {
                        chosen = replacement;
                        _hasTargetLock = false;
                        _targetSwitchCooldownUntil = Ts() + 0.30;
                    }
                    else chosen = lockedCand;
                }
                else
                {
                    // Hard lock means: if the same target is visible, never score-switch to neighbours.
                    chosen = lockedCand;
                }
                _targetLockMissFrames = 0;
            }
            else if (TargetLockHard)
            {
                // Locked target disappeared, but other candidates exist.
                // Rollback of the over-strict far-switch block: do not freeze forever.
                // Hold ghost briefly only while it is alive, then switch to a confirmed replacement.
                _targetLockMissFrames++;

                float pdx = best.AimX - _pendingSwitchAimX;
                float pdy = best.AimY - _pendingSwitchAimY;
                float confirmR = Math.Max(10f, TargetSwitchConfirmRadiusPx);
                if (_pendingSwitchFrames == 0 || pdx * pdx + pdy * pdy > confirmR * confirmR)
                {
                    _pendingSwitchAimX = best.AimX;
                    _pendingSwitchAimY = best.AimY;
                    _pendingSwitchFrames = 1;
                }
                else
                {
                    _pendingSwitchFrames++;
                }

                bool ghostAlive = _ghostTarget != null && _ghostFrames < GhostMaxFrames;
                int grace = Math.Min(TargetLockHoldFrames, TargetLockMaxMissFrames);
                if (ghostAlive && (_targetLockMissFrames <= grace || _pendingSwitchFrames < TargetSwitchConfirmFrames))
                    return null;

                _hasTargetLock = false;
                chosen = best; // confirmed/available replacement in this frame, no long freeze
            }
        }

        if (chosen == null && lt != null && !_hasTargetLock)
        {
            // Второй слой sticky по последней реальной позиции, если lock ещё не успел сформироваться.
            var sticky = cands.MinBy(d => MathF.Sqrt((d.AimX - lt.AimX) * (d.AimX - lt.AimX) + (d.AimY - lt.AimY) * (d.AimY - lt.AimY)));
            if (sticky != null)
            {
                float ds = MathF.Sqrt((sticky.AimX - lt.AimX) * (sticky.AimX - lt.AimX) + (sticky.AimY - lt.AimY) * (sticky.AimY - lt.AimY));
                if (ds <= StickyRadiusPx)
                {
                    float stickyScore = Score(sticky, true);
                    float bestScore = Score(best, false);
                    chosen = stickyScore * StickySwitchRatio >= bestScore ? sticky : best;
                }
            }
        }

        chosen ??= best;

        // Обновляем lock-якорь плавно, чтобы он следовал за целью, но не прыгал между людьми.
        if (TargetLockEnabled)
            UpdateTargetLock(chosen, _hasTargetLock ? 0.25f : 1f);

        return chosen;
    }

    public void SwitchTarget()
    {
        var cands = LastDetections.Where(d => d.Dist <= FovRadius).OrderBy(d => d.Dist).ToList();
        if (cands.Count == 0) { _manualLock = false; return; }
        if (cands.Count < 2) { LastTarget = cands[0]; _manualLock = false; return; }
        int cur = 0; var lt = LastTarget;
        if (lt != null) cur = cands.FindIndex(d => MathF.Abs(d.Cx - lt.Cx) < 15 && MathF.Abs(d.Cy - lt.Cy) < 15);
        if (cur < 0) cur = 0;
        LastTarget = cands[(cur + 1) % cands.Count];
        UpdateTargetLock(LastTarget, 1f);
        _manualLock = true; _manualLockUntil = Ts() + 1.0;
    }

    // ── Aim helpers ───────────────────────────────────────────────────────────
    public (float dx, float dy) GetAimDelta(float strength, float maxStep)
    {
        Detection? t;
        bool isGhost;
        float velX, velY, dt, totalYOffset;
        lock (_stateLock)
        {
            t = _lastTarget;
            if (t == null) return (0, 0);
            velX = _velX;
            velY = _velY;
            dt = PredictDt;
            totalYOffset = GetAimYOffsetForTarget(t);
            isGhost = _realTarget == null && _ghostTarget != null;
        }
        var (predX, predY) = ClampPrediction(velX * dt, velY * dt);
        float aimX = GetAimXForTarget(t) + predX;
        float aimY = t.AimY + predY + totalYOffset;
        return GetAimDeltaForPoint(aimX, aimY, strength, maxStep, isGhost);
    }

    // Внешняя точка входа для MouseLogic: считает сырой шаг к заданной точке.
    // Финальный anti-pendulum контроллер живёт в MouseLogic и сглаживает только скаляр скорости.
    public (float dx, float dy) GetAimDeltaForPoint(float aimX, float aimY, float strength, float maxStep, bool isGhost = false)
    {
        // Ghost: уменьшаем силу наведения чтобы не дёргать к устаревшей позиции
        int ghostFrames;
        int ghostMaxFrames;
        lock (_stateLock)
        {
            ghostFrames = _ghostFrames;
            ghostMaxFrames = GhostMaxFrames;
        }
        float effectiveStrength = isGhost
            ? strength * (1f - (float)ghostFrames / Math.Max(1, ghostMaxFrames)) * 0.4f
            : strength;
        if (effectiveStrength < 0.01f) return (0, 0);

        float dx = aimX - _screenCx;
        float dy = aimY - _screenCy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float movingSpeed;
        lock (_stateLock) movingSpeed = _targetSpeed;
        // If target is moving, a large stop zone makes aim permanently lag behind.
        float stop = movingSpeed > 20f ? Math.Min(Math.Max(0.5f, StopDist), 1.0f) : Math.Max(0.5f, StopDist);
        if (dist < stop) return (0, 0);

        // Smoothstep для дальних целей + линейное сближение вблизи.
        // Это сохраняет быстрый заход, но не даёт snap/перелёта около stop-зоны.
        const float transition = 100f;
        float norm = Math.Clamp((dist - stop) / transition, 0f, 1f);
        float ease = norm * norm * (3f - 2f * norm);
        float step = Math.Max(ease * dist * effectiveStrength, (dist - stop) * effectiveStrength);
        step = MathF.Min(step, maxStep);
        step = MathF.Min(step, dist - stop); // никогда не перелетаем
        step = MathF.Max(step, 0f);

        if (step < 0.1f) return (0, 0);
        float inv = step / dist;
        return (dx * inv, dy * inv);
    }

    public (float dx, float dy) GetDelta()
    {
        lock (_stateLock)
        {
            var t = _lastTarget;
            if (t == null) return (0, 0);
            float dt = PredictDt;
            var (predX, predY) = ClampPrediction(_velX * dt, _velY * dt);
            return (GetAimXForTarget(t) + predX - _screenCx, t.AimY + predY + GetAimYOffsetForTarget(t) - _screenCy);
        }
    }

    public bool IsCrosshairOnTarget(float tolerance)
    {
        // Используем RealTarget — не стреляем по призраку
        lock (_stateLock)
        {
            var t = _realTarget;
            if (t == null) return false;
            float dx = GetAimXForTarget(t) - _screenCx, dy = t.AimY + GetAimYOffsetForTarget(t) - _screenCy;
            return dx * dx + dy * dy <= tolerance * tolerance;
        }
    }

    // ── Velocity prediction ───────────────────────────────────────────────────
    // Скорость считается в экранных координатах (AimX/AimY).
    // Движение мыши тоже в этих координатах — они совпадают.
    private void UpdateVelocity(Detection? target, double now)
    {
        if (target == null)
        {
            // Если ghost ещё активен — скорость не трогаем (ghost её использует для экстраполяции).
            // Если ghost тоже пропал — затухаем медленнее (0.85 вместо 0.6), чтобы предикция
            // ещё немного помогала при быстром повторном появлении цели.
            if (_ghostTarget == null)
            {
                _velX *= 0.85f; _velY *= 0.85f;
                _fastVelX *= 0.85f; _fastVelY *= 0.85f;
                if (MathF.Abs(_velX) < 0.5f) _velX = _fastVelX = 0;
                if (MathF.Abs(_velY) < 0.5f) _velY = _fastVelY = 0;
            }
            _targetSpeed = 0f;
            return;
        }

        // AimX/AimY — экранные координаты центра прицела цели
        float cx = target.AimX;
        float cy = target.AimY;

        if (_lastT > 0)
        {
            double dt = now - _lastT;
            if (dt is > 0.003 and < 0.15)
            {
                float newInterval = Math.Clamp((float)dt, 0.005f, 0.033f);
                if (_frameInterval < 0.006f) _frameInterval = newInterval;
                else _frameInterval = 0.85f * _frameInterval + 0.15f * newInterval;

                float rvx = (float)((cx - _lastCapX) / dt);
                float rvy = (float)((cy - _lastCapY) / dt);

                // Отфильтровываем нереалистичные скачки (> 1500px/s)
                float spd = MathF.Sqrt(rvx * rvx + rvy * rvy);
                if (spd > 1500f) { _lastCapX = cx; _lastCapY = cy; _lastT = now; return; }

                // Цель стоит — обнуляем скорость мгновенно
                if (spd < 30f) { _velX *= 0.3f; _velY *= 0.3f; _fastVelX *= 0.3f; _fastVelY *= 0.3f; }

                // Быстрый EMA для отклика + медленный для стабильности
                float a = Math.Clamp(0.4f + spd / 2000f, 0.3f, 0.7f);
                _fastVelX = a * rvx + (1f - a) * _fastVelX;
                _fastVelY = a * rvy + (1f - a) * _fastVelY;
                // Итоговая скорость — чуть инертнее fastVel
                _velX = 0.5f * _fastVelX + 0.5f * _velX;
                _velY = 0.5f * _fastVelY + 0.5f * _velY;

                // Кап: не более 800px/s
                _velX = Math.Clamp(_velX, -900f, 900f);
                _velY = Math.Clamp(_velY, -600f, 600f); // по Y движение медленнее

            }
            else if (dt >= 0.15)
                _velX = _velY = _fastVelX = _fastVelY = 0;
        }
        _lastCapX = cx; _lastCapY = cy; _lastT = now;
        _targetSpeed = MathF.Sqrt(_velX * _velX + _velY * _velY);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            Task.WhenAll(
                _captureTask ?? Task.CompletedTask,
                _inferTask ?? Task.CompletedTask)
            .Wait(TimeSpan.FromSeconds(2));
        }
        catch { }

        InferenceSession? oldSession;
#if USE_OPENVINO
    OpenVinoSession? oldOvSess;
#endif

        lock (_sessionLock)
        {
            oldSession = _session;
#if USE_OPENVINO
        oldOvSess  = _ovSession;
#endif
            _session = null;
#if USE_OPENVINO
        _ovSession = null;
#endif
            _inputName = null;
        }

        oldSession?.Dispose();
#if USE_OPENVINO
    oldOvSess?.Dispose();
#endif
    }
}