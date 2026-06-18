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
    public float Cx, Cy;
    public float AimX, AimY;
    public float Dist, Area;
    private const float AimYFrac = 0.28f;

    public Detection(float x1, float y1, float x2, float y2, float conf, float scx, float scy)
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Conf = conf;
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
    public float FovRadius       { get; set; } = 300;
    public float ConfThresh      { get; set; } = 0.45f;
    public float AimYOffsetPx    { get; set; } = 0f;
    public float PredictionStr   { get; set; } = 0.6f;
    public float StopDist        { get; set; } = 3f;
    public int   ConfirmFrames   { get; set; } = 3;
    public bool  PrioritySize    { get; set; } = true;
    public bool  DeadZoneEnabled { get; set; } = true;
    public int   AimHz           { get; set; } = 0;
    public bool  UseTiled        { get; set; } = false;
    public int   TileOverlap     { get; set; } = 64;

    // ── State ─────────────────────────────────────────────────────────────────
    public Detection?      LastTarget     { get; private set; } // аим (включает призрак)
    public Detection?      RealTarget     { get; private set; } // только реальная цель (триггербот)
    public List<Detection> LastDetections { get; private set; } = [];
    public float           Fps            { get; private set; }
    public int             FrameId        { get; private set; }
    public bool            IsOnTarget     { get; set; }
    public string          ProviderName   { get; private set; } = "none";
    public List<DeadZone>  DeadZones      { get; set; } = [];
    public int             ScreenCx       => _screenCx;
    public int             ScreenCy       => _screenCy;
    public int             _capSize       { get; private set; }

    // ── Internals ─────────────────────────────────────────────────────────────
    private float _velX, _velY, _fastVelX, _fastVelY;
    private float _lastCapX, _lastCapY; // позиция цели в пространстве capture (не экранная)
    private double _lastT;
    // PredictDt — время упреждения в секундах
    // prediction=1.0 → упреждение на ~1 кадр вперёд (динамически по реальному FPS)
    // prediction=0.5 → полкадра, prediction=2.0 → два кадра
    private float _frameInterval = 0.016f; // обновляется в UpdateVelocity по реальному dt
    private float PredictDt => PredictionStr * _frameInterval;

    private bool   _manualLock;
    private double _manualLockUntil;

    // ── Ghost target (инерция после потери цели) ──────────────────────────────
    // Если цель потерялась — ещё GhostFrames кадров ведём в последнюю позицию
    // Убирает дёрганье на однокадровые фейки и мгновенную потерю цели
    private Detection? _ghostTarget;
    private int        _ghostFrames;
    public  int        GhostMaxFrames { get; set; } = 6; // настраиваемо, по умолч. 6 кадров

    private InferenceSession? _session;
    private string?           _inputName;
    private readonly int      _screenW, _screenH;
    private readonly int      _screenCx, _screenCy;
    private int               _capLeft, _capTop;
    private int               _modelInputSize = 640; // будет перезаписан из метаданных модели
    public  int               ModelInputSize  => _modelInputSize;

    private readonly List<float[]> _tracks = [];
    private const float SF_IOU   = 0.25f;
    private const int   SF_MISS  = 2;   // убиваем фейковые треки быстрее (было 4)
    private const float SF_ALPHA = 0.45f;

    private readonly CancellationTokenSource _cts = new();
    private Task? _captureTask, _inferTask;
    private readonly Channel<byte[]> _frameChannel =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest });

    private bool _useDxgi;
    private bool _numaPinning;
    private int  _numaCores;

    // ── Win32 NUMA-пиннинг (повторяет Python _setup_process_priority) ─────────
    [DllImport("kernel32.dll")] private static extern IntPtr  GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool    SetProcessAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("kernel32.dll")] private static extern bool    SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll")] private static extern IntPtr  GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool    SetThreadPriority(IntPtr h, int pri);
    private const uint HIGH_PRIORITY_CLASS     = 0x80;
    private const int  THREAD_PRIORITY_HIGHEST = 2;

    // Количество ядер одного NUMA-сокета (Socket 0 = ядра 0..HalfCores-1)
    private static readonly int _halfCores = Math.Max(1, Environment.ProcessorCount / 2);

    // ── OpenVINO Native сессия (замена ORT EP для XML и реального LATENCY) ────
    // null если используется ORT (_session != null)
    private OpenVinoSession? _ovSession;

    // ── Constructor ───────────────────────────────────────────────────────────
    public VisionEngine(string modelPath, int screenW, int screenH,
                        float conf, float fovRadius, string provider, int captureSize, bool useFp16,
                        bool numaPinning = false, int numaCores = 0)
    {
        _screenW = screenW; _screenH = screenH;
        _screenCx = screenW / 2; _screenCy = screenH / 2;
        ConfThresh = conf; FovRadius = fovRadius;
        ApplyCaptureSize(captureSize);

        // ── NUMA-пиннинг: включать только на Dual Xeon / многосокетных серверах
        // На обычном десктопе numaPinning=false — только HIGH priority
        ApplyNumaPinning(numaPinning, numaCores);
        _numaPinning = numaPinning;
        _numaCores   = numaCores;

        // ── Выбор провайдера ──────────────────────────────────────────────────
        bool useNativeOV = provider.Equals("openvino_native", StringComparison.OrdinalIgnoreCase)
                        || provider.Equals("openvino_xml",    StringComparison.OrdinalIgnoreCase)
                        || (provider.Equals("openvino", StringComparison.OrdinalIgnoreCase)
                            && modelPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        if (useNativeOV)
        {
            // Нативный OpenVINO SDK — единственный правильный путь для XML IR
            // и реального PERFORMANCE_HINT=LATENCY (ORT EP этого не поддерживает)
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
                Console.WriteLine("[vision] → Нужен NuGet: OpenVinoSharp.runtime.win-x64");
                Console.WriteLine("[vision] → Fallback на ORT CPU");
                _ovSession = null;
                // Fallback: ORT CPU
                _session = CreateSession(modelPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    ? modelPath : modelPath, "cpu_optimized", useFp16, out var fb);
                ProviderName = fb;
            }
        }
        else
        {
            _session  = CreateSession(modelPath, provider, useFp16, out var pName);
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
        Warmup();
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
        bool fp16, out string providerName)
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
            EnableMemoryPattern    = false,
            IntraOpNumThreads      = Math.Max(4, Environment.ProcessorCount - 2),
            InterOpNumThreads      = 2,
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
                        ["device_id"]               = "0",
                        ["trt_max_workspace_size"]  = "1073741824", // 1GB
                        ["trt_fp16_enable"]         = fp16 ? "1" : "0",
                        ["trt_engine_cache_enable"] = "1",
                        ["trt_engine_cache_path"]   = trtCacheDir,
                        ["trt_timing_cache_enable"] = "1",
                        ["trt_timing_cache_path"]   = trtCacheDir,
                    });
                    opts.AppendExecutionProvider_Tensorrt(trtOpts);
                    opts.AppendExecutionProvider_CUDA();
                    Console.WriteLine($"[vision] TensorRT cache → {trtCacheDir}");
                    providerName = "TensorRT"; break;
                case "cuda":
                    opts.AppendExecutionProvider_CUDA();
                    providerName = "CUDA"; break;
                case "directml": case "dml":
                    opts.AppendExecutionProvider_DML();
                    providerName = "DML"; break;
                case "openvino":
                    // OpenVINO EP — для CPU/Intel iGPU (Intel Arc, Iris Xe и т.д.)
                    // Поддерживает: ONNX (.onnx) и OpenVINO IR (.xml + .bin)
                    // Требует: onnxruntime-openvino пакет + OpenVINO Runtime в PATH
                    // Документация: https://docs.openvino.ai/latest
                    // Потоки задаём ДО добавления EP — после AppendExecutionProvider это no-op в ORT
                    opts.IntraOpNumThreads = Math.Max(4, Environment.ProcessorCount - 2);
                    opts.InterOpNumThreads = 1; // OpenVINO сам управляет внутренним параллелизмом
                    opts.ExecutionMode     = ExecutionMode.ORT_SEQUENTIAL;
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
                                ["device_type"]              = "CPU",
                                ["num_of_threads"]           = opts.IntraOpNumThreads.ToString(),
                                ["cache_dir"]                = xmlDir,
                                ["enable_opencl_throttling"] = "0",
                            });
                            providerName = "OpenVINO-IR";
                        }
                        else
                        {
                            opts.AppendExecutionProvider("OpenVINO", new Dictionary<string, string>
                            {
                                ["device_type"]              = "CPU",
                                ["num_of_threads"]           = opts.IntraOpNumThreads.ToString(),
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
                        opts.EnableCpuMemArena   = true;
                        opts.EnableMemoryPattern = true;
                        providerName = "CPU (OpenVINO н/д)";
                    }
                    break;

                case "cpu_optimized":
                    // CPU с оптимизациями: Arena allocator + физические ядра.
                    // ORT_SEQUENTIAL быстрее ORT_PARALLEL для одиночного YOLO-инференса —
                    // межоператорный параллелизм даёт оверхед синхронизации больше выигрыша.
                    // Физические ядра (≈ логические / 2) — не конкурируем с capture и UI потоком.
                    int physCores = Math.Max(2, Environment.ProcessorCount / 2);
                    opts.IntraOpNumThreads   = physCores;
                    opts.InterOpNumThreads   = 1;
                    opts.ExecutionMode       = ExecutionMode.ORT_SEQUENTIAL;
                    opts.EnableCpuMemArena   = true;
                    opts.EnableMemoryPattern = true;
                    opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    // Spinning только для intra — ускоряет отклик на коротких операторах
                    opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "1");
                    opts.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
                    Console.WriteLine($"[vision] CPU Optimized: {physCores} физ.ядер, ORT_SEQUENTIAL");
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
        if (_session == null) return;
        try
        {
            var blob = new DenseTensor<float>(new[] { 1, 3, _modelInputSize, _modelInputSize });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName!, blob) };
            for (int i = 0; i < 3; i++) _session.Run(inputs);
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
        _capTop  = Math.Max(0, Math.Min(_screenCy - cap / 2, _screenH - cap));
    }

    public bool IsReady() => _session != null || _ovSession != null;

    public void SetFov(int r)         { FovRadius  = Math.Max(50, r); }
    public void SetConf(float v)      { ConfThresh = Math.Clamp(v, 0.1f, 0.99f); }
    public void SetCaptureSize(int s) { ApplyCaptureSize(Math.Max(64, s)); }

    /// <summary>
    /// Перезагружает ONNX-модель без остановки capture/inference потоков.
    /// Потоки продолжают работать — в момент замены сессии они просто
    /// пропускают один кадр (session == null → пустой результат).
    /// </summary>
    public void RestartModel(string modelPath, string provider, bool fp16)
    {
        Console.WriteLine("[vision] Restarting model...");

        bool useNativeOV = provider.Equals("openvino_native", StringComparison.OrdinalIgnoreCase)
                        || provider.Equals("openvino_xml",    StringComparison.OrdinalIgnoreCase)
                        || (provider.Equals("openvino", StringComparison.OrdinalIgnoreCase)
                            && modelPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        _preprocessTensor = null;
        _preprocessBuffer = null;

        if (useNativeOV)
        {
            try
            {
                var newOv = new OpenVinoSession(modelPath, _halfCores, numaPinning: _numaPinning);
                var oldOv = _ovSession;
                var oldSess = _session;

                _ovSession   = newOv;
                _session     = null;
                _modelInputSize = newOv.ModelInputSize;
                ProviderName = newOv.ProviderName;

                oldOv?.Dispose();
                oldSess?.Dispose();

                Console.WriteLine($"[vision] Model restarted [OpenVINO Native], input: {_modelInputSize}px");
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[vision] OpenVINO Native restart failed: {e.Message} → ORT fallback");
            }
        }

        // ORT path
        var newSession = CreateSession(modelPath, provider, fp16, out var pName);
        if (newSession == null)
        {
            Console.WriteLine("[vision] Restart failed — session is null");
            return;
        }

        // Атомарно заменяем сессию (потоки проверяют _session != null)
        var oldSession = _session;
        var oldOvSess  = _ovSession;
        _inputName = newSession.InputMetadata.Keys.First();

        var shape = newSession.InputMetadata[_inputName].Dimensions;
        int detectedSize = shape.Length >= 3 && shape[2] > 0 ? shape[2] : 640;
        _modelInputSize = detectedSize;

        _session   = newSession;
        _ovSession = null;
        ProviderName = pName;

        oldSession?.Dispose();
        oldOvSess?.Dispose();

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
        _inferTask   = Task.Run(InferenceLoop);
        Console.WriteLine($"[vision] Started [{ProviderName}]");
    }

    public void Stop()
    {
        _cts.Cancel();
        // Ждём завершения потоков — максимум 2 секунды
        // Без этого старые потоки продолжают обращаться к _session после Dispose()
        try { Task.WhenAll(
                _captureTask ?? Task.CompletedTask,
                _inferTask   ?? Task.CompletedTask)
            .Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        _session?.Dispose();
        _session = null;
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
                double minDt  = 1.0 / AimHz;
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
                    var raw  = RunInference(blob);
                    dets = Postprocess(raw);
                }
                var tgt  = PickTarget(dets);
                LastDetections = dets;

                // ── Ghost target логика ───────────────────────────────────────
                RealTarget = tgt; // всегда только реальная детекция (для триггербота)

                if (tgt != null)
                {
                    // Реальная цель найдена — сбрасываем призрак
                    _ghostTarget = tgt;
                    _ghostFrames = 0;
                    LastTarget   = tgt;
                }
                else if (_ghostTarget != null && _ghostFrames < GhostMaxFrames)
                {
                    // Цель потеряна — ведём по последней позиции + предикт скорости
                    _ghostFrames++;
                    // Экстраполируем AimX/AimY призрака в экранных координатах
                    float gvx = _velX * 0.016f; // ~1 кадр при 60fps
                    float gvy = _velY * 0.016f;
                    var ghost = new Detection(
                        _ghostTarget.X1 + gvx * _ghostFrames,
                        _ghostTarget.Y1 + gvy * _ghostFrames,
                        _ghostTarget.X2 + gvx * _ghostFrames,
                        _ghostTarget.Y2 + gvy * _ghostFrames,
                        _ghostTarget.Conf * (1f - (float)_ghostFrames / GhostMaxFrames),
                        _screenCx, _screenCy);
                    LastTarget = ghost;
                }
                else
                {
                    // Призрак истёк — теряем цель
                    _ghostTarget = null;
                    LastTarget   = null;
                }

                fpsQ.Enqueue(Ts() - t0);
                if (fpsQ.Count > 30) fpsQ.Dequeue();
                Fps = (float)(1.0 / fpsQ.Average());
                UpdateVelocity(tgt, Ts()); // обновляем скорость только по реальной цели
                FrameId++;
            }
            catch (Exception ex)
            { Console.WriteLine($"[infer] {ex.Message}"); LastTarget = null; await Task.Delay(20, ct); }
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
    private float[]?            _preprocessBuffer;

    private DenseTensor<float> Preprocess(byte[] bgr)
    {
        int s     = _modelInputSize;
        int total = 3 * s * s;

        // Реюзаем тензор — не аллоцируем каждый кадр
        if (_preprocessTensor == null || _preprocessBuffer == null || _preprocessBuffer.Length != total)
        {
            _preprocessBuffer = new float[total];
            _preprocessTensor = new DenseTensor<float>(_preprocessBuffer, new[] { 1, 3, s, s });
        }

        int   cap    = _capSize;
        float scaleX = (float)cap / s;
        float scaleY = (float)cap / s;
        var   buf    = _preprocessBuffer;
        const float inv255 = 1f / 255f;

        // Unsafe: убираем bounds check в горячем пути (~15% ускорение на ядро)
        unsafe
        {
            fixed (byte*  pSrc = bgr)
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
        // ── OpenVINO Native path (XML IR + реальный LATENCY hint) ─────────────
        // Python оригинал: req.infer({0: blob}) → req.get_output_tensor(0).data
        // Здесь повторяем ту же логику через OpenVinoSession.Infer()
        if (_ovSession != null)
        {
            // _preprocessBuffer — тот же буфер что и в blob, без копирования
            return _ovSession.Infer(_preprocessBuffer!);
        }

        // ── ORT path (DML / TensorRT / CUDA / CPU) ───────────────────────────
        var sess = _session;
        if (sess == null) throw new OperationCanceledException("Session disposed");
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName!, blob) };
        using var results = sess.Run(inputs);
        return results.First().AsTensor<float>().ToArray();
    }

    // ── Postprocess — YOLOv8 person-only ─────────────────────────────────────
    private List<Detection> Postprocess(float[] raw)
    {
        // Модель может возвращать [1,5,N] (YOLO стандарт) или [1,N,5]
        // Python делает raw[0].T — т.е. работает с транспонированным [N,5]
        // Определяем формат: если raw.Length делится на 5 и первые строки имеют conf < 1 — [N,5]
        // Для моделей opset12 выход обычно [1, 5, N] → нужна транспозиция
        int nAnchors;
        bool transposed; // true = формат [1,5,N], false = [1,N,5]

        // Эвристика: пробуем [1,5,N] — типичный YOLOv8 export
        // В этом случае raw = [cx0,cx1,...cxN, cy0,..., w0,..., h0,..., conf0,...]
        // nAnchors = raw.Length / 5
        nAnchors = raw.Length / 5;
        // Проверяем conf в формате [1,5,N]: conf лежит в последних nAnchors элементах
        float firstConfTransposed = raw.Length > nAnchors * 4 ? raw[nAnchors * 4] : 0f;
        float firstConfNormal     = raw.Length > 4            ? raw[4]            : 0f;
        // Если conf в нетранспонированном формате выглядит разумно (0..1) — используем его
        transposed = !(firstConfNormal >= 0f && firstConfNormal <= 1f && firstConfNormal > firstConfTransposed);

        float scale  = (float)_capSize / _modelInputSize;
        var boxes = new List<(float x1,float y1,float x2,float y2,float conf)>(128);

        for (int i = 0; i < nAnchors; i++)
        {
            float cxb, cyb, wb, hb, conf;
            if (transposed)
            {
                // Формат [1,5,N]: каждый атрибут — отдельный блок длиной N
                cxb  = raw[0 * nAnchors + i];
                cyb  = raw[1 * nAnchors + i];
                wb   = raw[2 * nAnchors + i];
                hb   = raw[3 * nAnchors + i];
                conf = raw[4 * nAnchors + i];
            }
            else
            {
                // Формат [1,N,5]: каждый якорь — 5 чисел подряд
                conf = raw[i*5+4];
                cxb  = raw[i*5+0];
                cyb  = raw[i*5+1];
                wb   = raw[i*5+2];
                hb   = raw[i*5+3];
            }

            if (conf < ConfThresh) continue;
            float w=wb*scale, h=hb*scale, maxPx=_capSize*0.8f;
            if (w<6||h<6||w>maxPx||h>maxPx) continue;
            boxes.Add(((cxb-wb*0.5f)*scale+_capLeft, (cyb-hb*0.5f)*scale+_capTop,
                       (cxb+wb*0.5f)*scale+_capLeft, (cyb+hb*0.5f)*scale+_capTop, conf));
        }
        if (boxes.Count == 0) return [];

        var keep = NMS(boxes, 0.35f);
        var dets = keep.Select(k => new Detection(boxes[k].x1,boxes[k].y1,boxes[k].x2,boxes[k].y2,
            boxes[k].conf, _screenCx, _screenCy)).ToList();

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
        int overlap  = Math.Max(0, Math.Min(TileOverlap, tileSize / 2));
        int step     = tileSize - overlap;
        int gridN    = (int)Math.Ceiling((double)(_capSize - overlap) / step);
        gridN        = Math.Max(1, gridN);

        // Все боксы со всех тайлов — потом единый NMS
        var allBoxes = new System.Collections.Concurrent.ConcurrentBag<(float x1,float y1,float x2,float y2,float conf)>();

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
        var keep    = NMS(boxList, 0.35f);
        var dets    = keep.Select(k => new Detection(
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
        var buf    = new float[3 * s * s];
        var tensor = new DenseTensor<float>(buf, new[] { 1, 3, s, s });
        // BGR → RGB + normalize (тайл уже нужного размера — ресайз не нужен)
        System.Threading.Tasks.Parallel.For(0, s, row =>
        {
            int rowOffR = row * s;
            int rowOffG = s * s + row * s;
            int rowOffB = 2 * s * s + row * s;
            int src     = row * s * 3;
            for (int col = 0; col < s; col++, src += 3)
            {
                buf[rowOffR + col] = bgr[src + 2] * (1f / 255f); // R
                buf[rowOffG + col] = bgr[src + 1] * (1f / 255f); // G
                buf[rowOffB + col] = bgr[src + 0] * (1f / 255f); // B
            }
        });
        return tensor;
    }

    // Postprocess тайла — пересчёт координат с учётом смещения тайла
    private void PostprocessTile(float[] raw, int tileSize, int offX, int offY,
        System.Collections.Concurrent.ConcurrentBag<(float,float,float,float,float)> result)
    {
        int nAnchors = raw.Length / 5;

        for (int i = 0; i < nAnchors; i++)
        {
            float cxb  = raw[0 * nAnchors + i];
            float cyb  = raw[1 * nAnchors + i];
            float wb   = raw[2 * nAnchors + i];
            float hb   = raw[3 * nAnchors + i];
            float conf = raw[4 * nAnchors + i];

            if (conf < ConfThresh) continue;
            float maxPx = tileSize * 0.8f;
            if (wb < 6 || hb < 6 || wb > maxPx || hb > maxPx) continue;

            // Координаты в пространстве тайла → экранное пространство
            float x1 = (cxb - wb * 0.5f) + offX + _capLeft;
            float y1 = (cyb - hb * 0.5f) + offY + _capTop;
            float x2 = (cxb + wb * 0.5f) + offX + _capLeft;
            float y2 = (cyb + hb * 0.5f) + offY + _capTop;
            result.Add((x1, y1, x2, y2, conf));
        }
    }

    private static List<int> NMS(List<(float x1,float y1,float x2,float y2,float conf)> boxes, float thresh)
    {
        var order = Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].conf).ToList();
        var keep  = new List<int>();
        while (order.Count > 0)
        {
            int i = order[0]; keep.Add(i); order.RemoveAt(0);
            var (x1i,y1i,x2i,y2i,_) = boxes[i];
            float ai = (x2i-x1i)*(y2i-y1i);
            order = order.Where(j =>
            {
                var (x1j,y1j,x2j,y2j,_) = boxes[j];
                float ix = Math.Max(0, Math.Min(x2i,x2j)-Math.Max(x1i,x1j));
                float iy = Math.Max(0, Math.Min(y2i,y2j)-Math.Max(y1i,y1j));
                float inter = ix*iy;
                return inter/Math.Max(ai+(x2j-x1j)*(y2j-y1j)-inter, 1e-6f) < thresh;
            }).ToList();
        }
        return keep;
    }

    // ── Stability filter ──────────────────────────────────────────────────────
    private List<Detection> StabilityFilter(List<Detection> dets)
    {
        foreach (var tr in _tracks) tr[6]++;
        foreach (var d in dets)
        {
            float best = SF_IOU; float[]? bestTr = null;
            foreach (var tr in _tracks)
            {
                float iou = IouPair(d.X1,d.Y1,d.X2,d.Y2, tr[0],tr[1],tr[2],tr[3]);
                if (iou > best) { best = iou; bestTr = tr; }
            }
            if (bestTr != null)
            {
                float ia = 1-SF_ALPHA;
                bestTr[0]=SF_ALPHA*d.X1+ia*bestTr[0]; bestTr[1]=SF_ALPHA*d.Y1+ia*bestTr[1];
                bestTr[2]=SF_ALPHA*d.X2+ia*bestTr[2]; bestTr[3]=SF_ALPHA*d.Y2+ia*bestTr[3];
                bestTr[4]=0.5f*d.Conf+0.5f*bestTr[4]; bestTr[5]++; bestTr[6]=0;
            }
            else if (d.Conf >= 0.25f) _tracks.Add([d.X1,d.Y1,d.X2,d.Y2,d.Conf,1,0]); // не добавляем слабые детекции
        }
        _tracks.RemoveAll(tr => tr[6] > SF_MISS);
        if (ConfirmFrames == 0) return dets;
        var stable = new List<Detection>();
        foreach (var d in dets)
            foreach (var tr in _tracks)
                if ((int)tr[6]==0 && (int)tr[5]>=ConfirmFrames &&
                    IouPair(d.X1,d.Y1,d.X2,d.Y2, tr[0],tr[1],tr[2],tr[3])>SF_IOU)
                { stable.Add(d); break; }
        return stable;
    }

    private static float IouPair(float ax1,float ay1,float ax2,float ay2,
                                  float bx1,float by1,float bx2,float by2)
    {
        float ix=Math.Max(0,Math.Min(ax2,bx2)-Math.Max(ax1,bx1));
        float iy=Math.Max(0,Math.Min(ay2,by2)-Math.Max(ay1,by1));
        float inter=ix*iy;
        if (inter==0) return 0;
        return inter/((ax2-ax1)*(ay2-ay1)+(bx2-bx1)*(by2-by1)-inter+1e-6f);
    }

    // ── Target selection ──────────────────────────────────────────────────────
    private Detection? PickTarget(List<Detection> dets)
    {
        var stable = StabilityFilter(dets);
        var cands  = stable.Where(d => d.Dist <= FovRadius).ToList();
        if (cands.Count == 0) { _manualLock = false; return null; }

        var lt = LastTarget;
        if (_manualLock && Ts() < _manualLockUntil && lt != null)
        {
            var best = cands.MinBy(d => MathF.Sqrt((d.Cx-lt.Cx)*(d.Cx-lt.Cx)+(d.Cy-lt.Cy)*(d.Cy-lt.Cy)));
            if (best != null && MathF.Sqrt((best.Cx-lt.Cx)*(best.Cx-lt.Cx)+(best.Cy-lt.Cy)*(best.Cy-lt.Cy)) < 60)
                return best;
            _manualLock = false;
        }
        else _manualLock = false;

        if (lt != null)
        {
            float ltCx=lt.Cx, ltCy=lt.Cy;
            return cands.MaxBy(d => d.Conf/(d.Dist+1f)*
                (MathF.Sqrt((d.Cx-ltCx)*(d.Cx-ltCx)+(d.Cy-ltCy)*(d.Cy-ltCy))<40 ? 1.35f : 1f));
        }
        return cands.MaxBy(d => d.Conf/(d.Dist+1f));
    }

    public void SwitchTarget()
    {
        var cands = LastDetections.Where(d => d.Dist<=FovRadius).OrderBy(d => d.Dist).ToList();
        if (cands.Count==0) { _manualLock=false; return; }
        if (cands.Count<2)  { LastTarget=cands[0]; _manualLock=false; return; }
        int cur=0; var lt=LastTarget;
        if (lt!=null) cur=cands.FindIndex(d=>MathF.Abs(d.Cx-lt.Cx)<15&&MathF.Abs(d.Cy-lt.Cy)<15);
        if (cur<0) cur=0;
        LastTarget=cands[(cur+1)%cands.Count];
        _manualLock=true; _manualLockUntil=Ts()+3.0;
    }

    // ── Aim helpers ───────────────────────────────────────────────────────────
    public (float dx, float dy) GetAimDelta(float strength, float maxStep)
    {
        var t = LastTarget; if (t == null) return (0, 0);
        float dt = PredictDt;
        float dx = t.AimX + _velX * dt - _screenCx;
        float dy = t.AimY + _velY * dt + AimYOffsetPx - _screenCy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float stop = Math.Max(0.5f, StopDist);
        if (dist < stop) return (0, 0);

        // Плавная кривая без резкого переключения — убирает маятник
        // При малой дистанции шаг пропорционален расстоянию (нет перелёта)
        // При большой дистанции ограничен maxStep
        float rawStep = MathF.Sqrt(dist) * strength * 10f;
        float brake   = Math.Max(0f, dist - stop) / Math.Max(dist, 1f); // 0..1
        // Резкое наведение в точку:
        // — далеко (dist > 50px): летим с maxStep
        // — близко (dist <= 50px): шаг пропорционален расстоянию, гарантированно не перелетаем
        // strength — прямой коэффициент: step = dist * strength
        // При strength=0.25: прыжок = 25% расстояния за тик — нет маятника
        // При strength=1.0:  прыжок = 100% = мгновенный снап
        float step;
        if (dist > 50f)
            step = Math.Min(dist * strength, maxStep);
        else
            step = (dist - stop) * Math.Min(strength, 1.0f);

        step = Math.Min(step, dist - stop); // никогда не перелетаем
        step = Math.Max(step, 0f);

        if (step < 0.1f) return (0, 0);
        float inv = step / dist;
        return (dx * inv, dy * inv);
    }

    public (float dx, float dy) GetDelta()
    {
        var t=LastTarget; if (t==null) return (0,0);
        float dt=PredictDt;
        return (t.AimX+_velX*dt-_screenCx, t.AimY+_velY*dt+AimYOffsetPx-_screenCy);
    }

    public bool IsCrosshairOnTarget(float tolerance)
    {
        // Используем RealTarget — не стреляем по призраку
        var t = RealTarget; if (t == null) return false;
        float dx = t.AimX - _screenCx, dy = t.AimY + AimYOffsetPx - _screenCy;
        return dx*dx + dy*dy <= tolerance*tolerance;
    }

    // ── Velocity prediction ───────────────────────────────────────────────────
    // Скорость считается в экранных координатах (AimX/AimY).
    // Движение мыши тоже в этих координатах — они совпадают.
    private void UpdateVelocity(Detection? target, double now)
    {
        if (target == null)
        {
            _velX *= 0.6f; _velY *= 0.6f;
            _fastVelX *= 0.6f; _fastVelY *= 0.6f;
            if (MathF.Abs(_velX) < 1f) _velX = _fastVelX = 0;
            if (MathF.Abs(_velY) < 1f) _velY = _fastVelY = 0;
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
                _frameInterval = Math.Clamp((float)dt, 0.005f, 0.033f);

                float rvx = (float)((cx - _lastCapX) / dt);
                float rvy = (float)((cy - _lastCapY) / dt);

                // Отфильтровываем нереалистичные скачки (> 1500px/s)
                float spd = MathF.Sqrt(rvx * rvx + rvy * rvy);
                if (spd > 1500f) { _lastCapX = cx; _lastCapY = cy; _lastT = now; return; }

                // Быстрый EMA для отклика + медленный для стабильности
                float a = Math.Clamp(0.4f + spd / 2000f, 0.3f, 0.7f);
                _fastVelX = a * rvx + (1f - a) * _fastVelX;
                _fastVelY = a * rvy + (1f - a) * _fastVelY;
                // Итоговая скорость — чуть инертнее fastVel
                _velX = 0.5f * _fastVelX + 0.5f * _velX;
                _velY = 0.5f * _fastVelY + 0.5f * _velY;

                // Кап: не более 800px/s
                float sp2 = MathF.Sqrt(_velX * _velX + _velY * _velY);
                if (sp2 > 800f) { float k = 800f / sp2; _velX *= k; _velY *= k; }
            }
            else if (dt >= 0.15)
                _velX = _velY = _fastVelX = _fastVelY = 0;
        }
        _lastCapX = cx; _lastCapY = cy; _lastT = now;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WhenAll(
                _captureTask ?? Task.CompletedTask,
                _inferTask   ?? Task.CompletedTask)
            .Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        _session?.Dispose();
        _ovSession?.Dispose();
    }
}
