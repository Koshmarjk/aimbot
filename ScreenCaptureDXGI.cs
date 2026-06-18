// Vision/ScreenCaptureDXGI.cs
// DXGI Desktop Duplication — прямой захват GPU-фреймов без копирования через CPU.
// В ~10x быстрее GDI BitBlt, ~5x быстрее Windows.Graphics.Capture.
// Fallback → GDI BitBlt если DX11 недоступен.
using System.Runtime.InteropServices;

namespace HachBobAI.Vision;

// ─────────────────────────────────────────────────────────────────────────────
//  Интерфейс захвата (общий)
// ─────────────────────────────────────────────────────────────────────────────

public interface IScreenCapture : IDisposable
{
    /// <summary>Захватить регион экрана в BGR-буфер.</summary>
    bool TryGrab(int left, int top, int width, int height, byte[] dst);
    string ProviderName { get; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  DXGI Desktop Duplication
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DXGICapture : IScreenCapture
{
    // ─── DXGI / D3D11 guids ──────────────────────────────────────────────────
    private static readonly Guid IID_IDXGIFactory1   = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIAdapter    = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
    private static readonly Guid IID_IDXGIOutput1    = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_IDXGIOutputDuplication = new("191cfac3-a341-470d-b26e-a864f428319c");
    private static readonly Guid IID_IDXGISurface    = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    private static readonly Guid IID_ID3D11Device    = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid IID_ID3D11DeviceContext = new("c0bfa96c-e089-44fb-8eaf-26f8796190da");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ─── D3D11 enums / structs ────────────────────────────────────────────────
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D11_SDK_VERSION        = 7;
    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int D3D11_USAGE_STAGING      = 3;
    private const int D3D11_CPU_ACCESS_READ    = 0x20000;
    private const int D3D11_MAP_READ           = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int  RectsCoalesced;
        public int  ProtectedContentMasked;
        public uint PointerShapeBufferSize;
        public int  PointerPosition_X;
        public int  PointerPosition_Y;
        public int  PointerPosition_Visible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeChanged;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height;
        public uint MipLevels, ArraySize;
        public int  Format;
        public uint SampleCount, SampleQuality;
        public int  Usage;
        public int  BindFlags;
        public int  CPUAccessFlags;
        public int  MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint   RowPitch;
        public uint   DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX
    {
        public uint left, top, front, right, bottom, back;
    }

    // ─── P/Invoke ─────────────────────────────────────────────────────────────
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    // COM interop helpers
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid rclsid, IntPtr inner,
        uint ctx, in Guid riid, out IntPtr ppv);

    // ─── COM vtable offsets (x64 ABI) ────────────────────────────────────────
    // All COM interfaces: vtable[0]=QueryInterface, [1]=AddRef, [2]=Release
    // IDXGIFactory1::EnumAdapters = vtable[7]
    // IDXGIAdapter::EnumOutputs   = vtable[7]
    // IDXGIOutput::QueryInterface → IDXGIOutput1
    // IDXGIOutput1::DuplicateOutput = vtable[22]
    // IDXGIOutputDuplication::AcquireNextFrame = vtable[8]
    //                         ::ReleaseFrame   = vtable[14]
    // ID3D11Device::CreateTexture2D = vtable[5]
    // ID3D11DeviceContext::CopySubresourceRegion = vtable[46]
    //                    ::Map    = vtable[14]
    //                    ::Unmap  = vtable[15]

    private delegate int EnumAdapters_d(IntPtr self, uint idx, out IntPtr ppAdapter);
    private delegate int EnumOutputs_d(IntPtr self, uint idx, out IntPtr ppOutput);
    private delegate int QueryInterface_d(IntPtr self, in Guid riid, out IntPtr ppv);
    private delegate int DuplicateOutput_d(IntPtr self, IntPtr pDevice, out IntPtr ppDupl);
    private delegate int AcquireNextFrame_d(IntPtr self, uint TimeoutMs, out DXGI_OUTDUPL_FRAME_INFO fi, out IntPtr ppDesktopResource);
    private delegate int ReleaseFrame_d(IntPtr self);
    private delegate void Release_d(IntPtr self);
    private delegate int CreateTexture2D_d(IntPtr self, in D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr ppTex);
    private delegate int CopySubresourceRegion_d(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, in D3D11_BOX box);
    private delegate int Map_d(IntPtr self, IntPtr resource, uint sub, int mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);
    private delegate void Unmap_d(IntPtr self, IntPtr resource, uint sub);

    private static T GetVtable<T>(IntPtr obj, int idx) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(obj);
        IntPtr fn     = Marshal.ReadIntPtr(vtable, idx * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    // ─── Fields ───────────────────────────────────────────────────────────────
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _duplication;
    private IntPtr _stagingTex;
    private uint   _stagingW, _stagingH;
    private bool   _disposed;
    private int    _stagingFailCount;

    public string ProviderName => "DXGI";

    public DXGICapture()
    {
        Init();
    }

    private void Init()
    {
        // Create D3D11 device
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            0, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out _device, out _, out _context);
        if (hr < 0) throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr:X8}");

        // Get DXGI device → adapter → output → output1
        var qi = GetVtable<QueryInterface_d>(_device, 0);
        qi(_device, IID_IDXGIAdapter, out var dxgiDevice);
        // Actually we need IDXGIDevice → GetAdapter, but simpler: enumerate via factory
        hr = CreateDXGIFactory1(IID_IDXGIFactory1, out var factory);
        if (hr < 0) throw new InvalidOperationException($"CreateDXGIFactory1 failed: 0x{hr:X8}");

        var enumAdapters = GetVtable<EnumAdapters_d>(factory, 7);
        hr = enumAdapters(factory, 0, out var adapter);
        GetVtable<Release_d>(factory, 2)(factory);
        if (hr < 0) throw new InvalidOperationException("No adapters found");

        var enumOutputs = GetVtable<EnumOutputs_d>(adapter, 7);
        hr = enumOutputs(adapter, 0, out var output);
        GetVtable<Release_d>(adapter, 2)(adapter);
        if (hr < 0) throw new InvalidOperationException("No outputs found");

        // QI → IDXGIOutput1
        var qiOut = GetVtable<QueryInterface_d>(output, 0);
        qiOut(output, IID_IDXGIOutput1, out var output1);
        GetVtable<Release_d>(output, 2)(output);

        // DuplicateOutput
        var dupOut = GetVtable<DuplicateOutput_d>(output1, 22);
        hr = dupOut(output1, _device, out _duplication);
        GetVtable<Release_d>(output1, 2)(output1);
        if (hr < 0) throw new InvalidOperationException($"DuplicateOutput failed: 0x{hr:X8}");
    }

    private void EnsureStaging(uint w, uint h)
    {
        if (_stagingTex != IntPtr.Zero && _stagingW == w && _stagingH == h)
        { _stagingFailCount = 0; return; }
        if (_stagingTex != IntPtr.Zero) GetVtable<Release_d>(_stagingTex, 2)(_stagingTex);

        // Защита от некорректных размеров
        if (w == 0 || h == 0 || w > 3840 || h > 2160)
            throw new InvalidOperationException($"EnsureStaging: bad size {w}x{h}");

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width       = w, Height      = h,
            MipLevels   = 1, ArraySize   = 1,
            Format      = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1, SampleQuality = 0,
            Usage       = D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        var createTex = GetVtable<CreateTexture2D_d>(_device, 5);
        int hr = createTex(_device, desc, IntPtr.Zero, out _stagingTex);
        if (hr < 0)
        {
            _stagingFailCount++;
            // Логируем только первый раз и каждые 100 провалов — не спамим
            if (_stagingFailCount == 1 || _stagingFailCount % 100 == 0)
                Console.WriteLine($"[capture] CreateTexture2D failed: 0x{hr:X8} (x{_stagingFailCount})");
            throw new InvalidOperationException($"CreateTexture2D failed: 0x{hr:X8}");
        }
        _stagingFailCount = 0;
        _stagingW = w; _stagingH = h;
    }

    public bool TryGrab(int left, int top, int width, int height, byte[] dst)
    {
        var acquire = GetVtable<AcquireNextFrame_d>(_duplication, 8);
        int hr = acquire(_duplication, 33, out var fi, out var desktopRes);

        // DXGI_ERROR_DEVICE_REMOVED (0x887A0005) или DXGI_ERROR_ACCESS_LOST (0x887A0026)
        if (hr == unchecked((int)0x887A0005) || hr == unchecked((int)0x887A0026))
        {
            _stagingFailCount++;
            if (_stagingFailCount == 1 || _stagingFailCount % 100 == 0)
                Console.WriteLine($"[capture] Device lost (x{_stagingFailCount}), reinitializing...");
            Thread.Sleep(200);
            try { Reinit(); _stagingFailCount = 0; }
            catch (Exception ex)
            {
                if (_stagingFailCount % 100 == 0)
                    Console.WriteLine($"[capture] Reinit failed: {ex.Message}");
            }
            return false;
        }

        if (hr < 0) return false;  // timeout or mode change

        try
        {
            // QI resource → IDXGISurface → ID3D11Texture2D
            var qi = GetVtable<QueryInterface_d>(desktopRes, 0);
            qi(desktopRes, IID_ID3D11Texture2D, out var srcTex);
            GetVtable<Release_d>(desktopRes, 2)(desktopRes);

            uint uw = (uint)width, uh = (uint)height;
            EnsureStaging(uw, uh);

            // Copy region to staging texture
            var box = new D3D11_BOX { left=(uint)left, top=(uint)top, front=0,
                                      right=(uint)(left+width), bottom=(uint)(top+height), back=1 };
            var copyRegion = GetVtable<CopySubresourceRegion_d>(_context, 46);
            copyRegion(_context, _stagingTex, 0, 0, 0, 0, srcTex, 0, box);
            GetVtable<Release_d>(srcTex, 2)(srcTex);

            // Map staging → read pixels
            var mapFn = GetVtable<Map_d>(_context, 14);
            hr = mapFn(_context, _stagingTex, 0, D3D11_MAP_READ, 0, out var mapped);
            if (hr < 0) return false;

            unsafe
            {
                byte* src  = (byte*)mapped.pData;
                int   rowPitch = (int)mapped.RowPitch;
                int   dstIdx = 0;
                for (int row = 0; row < height; row++)
                {
                    byte* rowPtr = src + row * rowPitch;
                    for (int col = 0; col < width; col++)
                    {
                        // BGRA → BGR (drop A)
                        dst[dstIdx++] = rowPtr[col * 4 + 0]; // B
                        dst[dstIdx++] = rowPtr[col * 4 + 1]; // G
                        dst[dstIdx++] = rowPtr[col * 4 + 2]; // R
                    }
                }
            }

            var unmap = GetVtable<Unmap_d>(_context, 15);
            unmap(_context, _stagingTex, 0);
            return true;
        }
        finally
        {
            GetVtable<ReleaseFrame_d>(_duplication, 14)(_duplication);
        }
    }

    private void Reinit()
    {
        // Освобождаем старые ресурсы
        if (_stagingTex  != IntPtr.Zero) { GetVtable<Release_d>(_stagingTex,  2)(_stagingTex);  _stagingTex  = IntPtr.Zero; }
        if (_duplication != IntPtr.Zero) { GetVtable<Release_d>(_duplication, 2)(_duplication); _duplication = IntPtr.Zero; }
        if (_context     != IntPtr.Zero) { GetVtable<Release_d>(_context,     2)(_context);     _context     = IntPtr.Zero; }
        if (_device      != IntPtr.Zero) { GetVtable<Release_d>(_device,      2)(_device);      _device      = IntPtr.Zero; }
        _stagingW = 0; _stagingH = 0;
        Init();
        Console.WriteLine("[capture] DXGI reinitialized ✓");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stagingTex  != IntPtr.Zero) GetVtable<Release_d>(_stagingTex, 2)(_stagingTex);
        if (_duplication != IntPtr.Zero) GetVtable<Release_d>(_duplication, 2)(_duplication);
        if (_context     != IntPtr.Zero) GetVtable<Release_d>(_context, 2)(_context);
        if (_device      != IntPtr.Zero) GetVtable<Release_d>(_device, 2)(_device);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  GDI BitBlt fallback
// ─────────────────────────────────────────────────────────────────────────────

public sealed class GDICapture : IScreenCapture
{
    [DllImport("gdi32.dll")]  static extern bool BitBlt(IntPtr hdc, int xd, int yd, int w, int h, IntPtr src, int xs, int ys, uint rop);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")]  static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]  static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern int GetDIBits(IntPtr hdc, IntPtr bmp, int start, int lines, byte[] bits, ref BmpInfo bi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    struct BmpInfoHeader { public int biSize, biWidth, biHeight, biPlanes_biBitCount, biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct BmpInfo { public BmpInfoHeader bmiHeader; }

    private const uint SRCCOPY = 0x00CC0020;
    private IntPtr _screenDC, _memDC, _bmp;

    public string ProviderName => "GDI";

    public GDICapture()
    {
        _screenDC = GetDC(IntPtr.Zero);
        _memDC    = CreateCompatibleDC(_screenDC);
    }

    public bool TryGrab(int left, int top, int width, int height, byte[] dst)
    {
        if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
        _bmp = CreateCompatibleBitmap(_screenDC, width, height);
        SelectObject(_memDC, _bmp);
        BitBlt(_memDC, 0, 0, width, height, _screenDC, left, top, SRCCOPY);
        var bi = new BmpInfo { bmiHeader = new BmpInfoHeader {
            biSize = Marshal.SizeOf<BmpInfoHeader>(),
            biWidth = width, biHeight = -height,
            biPlanes_biBitCount = (1 << 16) | 24 }};
        return GetDIBits(_memDC, _bmp, 0, height, dst, ref bi, 0) > 0;
    }

    public void Dispose()
    {
        if (_bmp     != IntPtr.Zero) DeleteObject(_bmp);
        if (_memDC   != IntPtr.Zero) DeleteDC(_memDC);
        if (_screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDC);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Фабрика: DXGI → GDI fallback
// ─────────────────────────────────────────────────────────────────────────────

public static class ScreenCaptureFactory
{
    private static DXGICapture? _dxgi;
    private static GDICapture?  _gdi;

    // Раздельные буферы для DXGI и GDI — не пересекаются
    private static readonly byte[] _dxgiBuf = new byte[3840 * 2160 * 3];
    private static readonly byte[] _gdiBuf  = new byte[3840 * 2160 * 3];

    // ── Static helpers used by VisionEngine ──────────────────────────────────
    public static bool TryInitDxgi()
    {
        // Обязательно освобождаем старый экземпляр перед созданием нового.
        // DuplicateOutput разрешает только одну активную сессию на процесс —
        // без Dispose() второй вызов вернёт DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
        // и при третьем рестарте процесс крашнет.
        try { _dxgi?.Dispose(); } catch { }
        _dxgi = null;

        try
        {
            _dxgi = new DXGICapture();
            Console.WriteLine("[capture] DXGI Desktop Duplication ✓");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[capture] DXGI недоступен ({ex.Message}) → GDI fallback");
            try { _gdi ??= new GDICapture(); } catch { }
            return false;
        }
    }

    public static byte[]? DxgiGrab(int left, int top, int width, int height)
    {
        if (_dxgi == null) return null;
        int size = width * height * 3;
        if (_dxgiBuf.Length < size) return null;
        return _dxgi.TryGrab(left, top, width, height, _dxgiBuf) ? _dxgiBuf[..size] : null;
    }

    public static byte[]? GdiGrab(int left, int top, int width, int height)
    {
        _gdi ??= new GDICapture();
        int size = width * height * 3;
        if (_gdiBuf.Length < size) return null;
        return _gdi.TryGrab(left, top, width, height, _gdiBuf) ? _gdiBuf[..size] : null;
    }

    /// <summary>Освободить все ресурсы захвата (вызывать при выходе из приложения).</summary>
    public static void Dispose()
    {
        try { _dxgi?.Dispose(); } catch { }
        try { _gdi?.Dispose();  } catch { }
        _dxgi = null;
        _gdi  = null;
    }
}
