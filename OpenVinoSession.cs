// OpenVinoSession.cs — нативный OpenVINO SDK для C# (замена ORT EP для XML/BIN)
// ─────────────────────────────────────────────────────────────────────────────
// ЗАВИСИМОСТИ — добавить в HachBobAI.csproj:
//   <PackageReference Include="OpenVINO.CSharp.API"  Version="2025.0.0.1"/>
//   <PackageReference Include="OpenVINO.runtime.win" Version="2024.4.0.1"/>
//
// После добавления: dotnet restore
// ─────────────────────────────────────────────────────────────────────────────
#if USE_OPENVINO
using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenVinoSharp;   // из NuGet: OpenVINO.CSharp.API

namespace HachBobAI.Vision;

public sealed class OpenVinoSession : IDisposable
{
    // ── Win32: HIGH priority (NUMA delegated to OpenVINO) ──────────────────────
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool   SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool   SetThreadPriority(IntPtr h, int pri);
    private const uint HIGH_PRIORITY_CLASS     = 0x00000080;
    private const int  THREAD_PRIORITY_HIGHEST = 2;

    // ── OpenVINO объекты ──────────────────────────────────────────────────────
    private Core?          _core;
    private CompiledModel? _compiled;
    private InferRequest?  _reqA;
    private InferRequest?  _reqB;
    private bool           _toggle;

    // Размеры тензоров (кешируем чтобы не спрашивать каждый кадр)
    private int _inputLength;   // 3 * H * W (число float элементов входа)
    private int _outputLength;  // число float элементов выхода

    public int    ModelInputSize { get; private set; } = 640;
    public string ProviderName   { get; private set; } = "OpenVINO";

    // ─────────────────────────────────────────────────────────────────────────
    public OpenVinoSession(string modelPath, int halfCores = 0, string cacheDir = "ov_cache", bool numaPinning = false)
    {
        if (halfCores <= 0)
            halfCores = Math.Max(1, Environment.ProcessorCount / 2);

        ApplyNumaPinning(halfCores, numaPinning);

        string loadPath = ResolveModelPath(modelPath);
        Console.WriteLine($"[ov] Загружаем: {loadPath}");

        _core = new Core();
        Directory.CreateDirectory(cacheDir);

        // Устанавливаем свойства перед компиляцией через словарь конфига
        // В OpenVINO.CSharp.API 2025.x set_property принимает только 2 аргумента (без device)
        // Правильный способ — передать конфиг в compile_model
        var config = new Dictionary<string, string>
        {
            ["PERFORMANCE_HINT"]      = "LATENCY",
            ["NUM_STREAMS"]           = "1",
            ["INFERENCE_NUM_THREADS"] = halfCores.ToString(),
            ["ENABLE_CPU_PINNING"]    = numaPinning ? "NUMA" : "NO",
            ["CACHE_DIR"]             = Path.GetFullPath(cacheDir),
        };

        // Читаем модель и компилируем
        using var model = _core.read_model(loadPath);

        // Определяем ModelInputSize из метаданных ПЕРЕД compile (пока есть Model)
        DetectInputSize(model);

        // compile_model с конфигом — свойства передаются напрямую
        _compiled = _core.compile_model(model, "CPU", config);
        _reqA     = _compiled.create_infer_request();
        _reqB     = _compiled.create_infer_request();
        _toggle   = false;

        // Кешируем размеры тензоров
        CacheTensorSizes();

        Warmup();

        ProviderName = $"OpenVINO CPU×{halfCores} LATENCY";
        Console.WriteLine($"[ov] ✓  {ProviderName}  input={ModelInputSize}px");
    }

    // ── Inference ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Выполняет inference. Чередует reqA/reqB.
    /// inputData — плоский float[] [1,3,H,W], возвращает плоский float[] выхода.
    /// </summary>
    public float[] Infer(float[] inputData)
    {
        if (_reqA == null || _reqB == null)
            throw new ObjectDisposedException(nameof(OpenVinoSession));

        var req = _toggle ? _reqB : _reqA;
        _toggle = !_toggle;

        // Получаем входной тензор и копируем данные
        using var inTensor = req.get_input_tensor();
        // get_data<T>(int length) — нужно передать количество элементов
        var dst = inTensor.get_data<float>(_inputLength);
        inputData.AsSpan(0, _inputLength).CopyTo(dst);

        req.infer();

        // Читаем выход
        using var outTensor = req.get_output_tensor();
        var src = outTensor.get_data<float>(_outputLength);
        return src.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static void ApplyNumaPinning(int halfCores, bool enabled)
    {
        try
        {
            var h = GetCurrentProcess();
            SetPriorityClass(h, HIGH_PRIORITY_CLASS);
            // SetProcessAffinityMask удалён — ломал DXGI на Dual Xeon
            // OpenVINO сам привяжет потоки через ENABLE_CPU_PINNING=NUMA
            Console.WriteLine("[ov] Priority: HIGH. Thread pinning delegated to OpenVINO.");
        }
        catch (Exception e) { Console.WriteLine($"[ov] NUMA pinning: {e.Message}"); }
    }

    public static void SetInferenceThreadPriority()
    {
        try { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST); }
        catch { }
    }

    private static string ResolveModelPath(string path)
    {
        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"[ov] XML не найден: {path}");
            string bin = Path.ChangeExtension(path, ".bin");
            if (!File.Exists(bin))
                throw new FileNotFoundException($"[ov] .bin не найден рядом с XML: {bin}");
            Console.WriteLine($"[ov] IR: {Path.GetFileName(path)} + {Path.GetFileName(bin)}");
            return path;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        foreach (var candidate in new[]
        {
            Path.Combine("ov_int8",  $"{name}_int8_416.xml"),
            Path.Combine("ov_int8",  $"{name}_int8.xml"),
            Path.Combine("ov_fp16",  $"{name}_fp16.xml"),
            Path.Combine("ov_cache", $"{name}.xml"),
        })
        {
            if (File.Exists(candidate) && File.Exists(Path.ChangeExtension(candidate, ".bin")))
            {
                Console.WriteLine($"[ov] Найден IR: {candidate}");
                return candidate;
            }
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"[ov] Модель не найдена: {path}");

        Console.WriteLine("[ov] ONNX → OV конвертирует на лету");
        return path;
    }

    private void DetectInputSize(Model model)
    {
        try
        {
            // PartialShape.to_string() возвращает строку вида "[1,3,416,416]"
            // Парсим её — это самый надёжный способ для любой версии API
            var input      = model.input();
            var shapeStr   = input.get_partial_shape().to_string(); // "[1,3,416,416]"
            Console.WriteLine($"[ov] Input partial shape: {shapeStr}");

            // Убираем скобки и разбиваем по запятой
            shapeStr = shapeStr.Trim('[', ']', '{', '}');
            var parts = shapeStr.Split(',');
            if (parts.Length >= 3 && int.TryParse(parts[2].Trim(), out int h) && h > 0)
                ModelInputSize = h;

            Console.WriteLine($"[ov] ModelInputSize = {ModelInputSize}px");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ov] DetectInputSize: {e.Message} → используем {ModelInputSize}px");
        }
    }

    private void CacheTensorSizes()
    {
        // Кешируем размеры чтобы передавать в get_data<T>(length) каждый кадр
        _inputLength  = 3 * ModelInputSize * ModelInputSize;

        // Размер выхода читаем из скомпилированной модели
        try
        {
            if (_reqA != null)
            {
                using var t = _reqA.get_output_tensor();
                // Пробуем получить размер через Shape
                var shape    = _compiled!.output().get_shape();
                long total   = 1;
                foreach (var d in shape.ToList()) total *= d;
                _outputLength = (int)total;
                Console.WriteLine($"[ov] Output size: {_outputLength} floats");
            }
        }
        catch
        {
            // Fallback: оцениваем по типичному YOLO выходу [1, 5, N]
            // где N ≈ (inputSize/8)^2 + (inputSize/16)^2 + (inputSize/32)^2
            int s = ModelInputSize;
            _outputLength = 5 * ((s/8)*(s/8) + (s/16)*(s/16) + (s/32)*(s/32));
            Console.WriteLine($"[ov] Output size (estimated): {_outputLength} floats");
        }
    }

    private void Warmup(int n = 5)
    {
        if (_reqA == null) return;
        try
        {
            var dummy = new float[_inputLength];
            for (int i = 0; i < n; i++)
            {
                var req = (i % 2 == 0) ? _reqA : _reqB!;
                using var t = req.get_input_tensor();
                dummy.AsSpan().CopyTo(t.get_data<float>(_inputLength));
                req.infer();
            }
            Console.WriteLine("[ov] Warmup ✓");
        }
        catch (Exception e) { Console.WriteLine($"[ov] Warmup: {e.Message}"); }
    }

    public void Dispose()
    {
        _reqA?.Dispose();
        _reqB?.Dispose();
        _compiled?.Dispose();
        _core?.Dispose();
        _reqA = null; _reqB = null; _compiled = null; _core = null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ВЕРСИИ ПАКЕТОВ (проверено совместимость):
//   OpenVINO.CSharp.API  2025.0.0.1  — даёт namespace OpenVinoSharp
//   OpenVINO.runtime.win 2024.4.0.1  — нативные dll для Windows
//
// В HachBobAI.csproj:
//   <PackageReference Include="OpenVINO.CSharp.API"  Version="2025.0.0.1"/>
//   <PackageReference Include="OpenVINO.runtime.win" Version="2024.4.0.1"/>
//
// Провайдеры для активации OpenVinoSession: "openvino_native", "openvino_xml",
// или "openvino" при передаче .xml пути модели.
// ─────────────────────────────────────────────────────────────────────────────
#endif