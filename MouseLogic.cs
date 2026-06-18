// Input/MouseLogic.cs — SendInput (атомарный Win32 вызов, минимальная латентность)
using System.Runtime.InteropServices;
using HachBobAI.Config;
using HachBobAI.Vision;
using SharpHook;
using SharpHook.Native;

namespace HachBobAI.Input;

public sealed class MouseLogic : IDisposable
{
    // ── Settings ──────────────────────────────────────────────────────────────
    public float  Strength    { get; set; } = 0.15f;
    public float  MaxStep     { get; set; } = 24f;
    public float  Smooth      { get; set; } = 0.35f;
    public int    AimHz       { get; set; } = 0;
    public bool   ToggleMode  { get; set; } = false;
    public bool   Enabled     { get; set; } = true;

    // Triggerbot
    public bool  TbEnabled   { get; set; } = false;
    public float TbTolerance { get; set; } = 12f;
    public float TbDelayMin  { get; set; } = 0f;
    public float TbDelayMax  { get; set; } = 0f;
    public bool  TbAimOnly   { get; set; } = true;

    // Rangefinder
    public bool                              RfEnabled          { get; set; } = false;
    public List<(float bboxH, float yOffset)> RfTable           { get; set; } = [];
    public float                             RfBaseYOffset       { get; set; } = 0f;
    public float                             RfCurrentOffset     { get; private set; }
    public bool                              RfPresetControlled  { get; set; } = false;

    // Binds
    public string BindAim          { get; set; } = "x2";
    public string BindSwitchTarget { get; set; } = "x1";
    public string BindToggle       { get; set; } = "insert";
    public string BindOverlay      { get; set; } = "f4";
    public string BindHideGui      { get; set; } = "home";
    public string BindDeadZone     { get; set; } = "v";
    public string BindTriggerbot   { get; set; } = "\\";
    public string BindRangefinder  { get; set; } = "r";
    public string BindExit         { get; set; } = "f12";

    public Dictionary<string, int> PresetBinds     { get; set; } = [];
    public Action<int>?  OnPresetApply;
    public Action<bool>? OnToggleEnabled;
    public Action?       OnShowOverlay;
    public Action?       OnHideGui;
    public Action?       OnDeadZoneToggle;
    public Action?       OnTriggerbotToggle;
    public Action?       OnRangefinderToggle;
    public Action?       OnExit;

    public bool AimHeld { get; private set; }
    public bool LmbHeld { get; private set; }

    public VisionEngine? Vision { get; set; }

    // ── Win32 SendInput (заменяет устаревший mouse_event) ────────────────────
    // SendInput — одна атомарная операция, единственный системный вызов,
    // меньше накладных расходов и латентности чем mouse_event

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int    dx, dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public InputUnion u; }

    private const uint MOUSEEVENTF_MOVE  = 0x0001;
    private const uint MOUSEEVENTF_LDOWN = 0x0002;
    private const uint MOUSEEVENTF_LUP   = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    private static readonly int InputSize = Marshal.SizeOf<Input>();

    // ── EMA state ─────────────────────────────────────────────────────────────
    private float _emaMx, _emaMy;
    private bool  _emaActive;
    private readonly Random _rng = new();
    private const float HumanJitter = 0.35f;

    // ── Hook ──────────────────────────────────────────────────────────────────
    private SimpleGlobalHook? _hook;
    private Task?             _hookTask;
    private readonly CancellationTokenSource _cts = new();

    public MouseLogic() { timeBeginPeriod(1); }

    public void Start()
    {
        _hook = new SimpleGlobalHook();
        _hook.MousePressed  += OnMousePressed;
        _hook.MouseReleased += OnMouseReleased;
        _hook.KeyPressed    += OnKeyPressed;
        _hook.KeyReleased   += OnKeyReleased;
        _hookTask = _hook.RunAsync();

        Task.Run(AimLoop,        _cts.Token);
        Task.Run(TriggerbotLoop, _cts.Token);
        Console.WriteLine("[input] Started — SendInput");
    }

    // ── Mouse input — SendInput ───────────────────────────────────────────────
    private void MouseMove(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var inp = new Input[1];
        inp[0].type       = 0;
        inp[0].u.mi.dx    = dx;
        inp[0].u.mi.dy    = dy;
        inp[0].u.mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, inp, InputSize);
    }

    private void MouseClick()
    {
        var down = new Input[1];
        down[0].type         = 0;
        down[0].u.mi.dwFlags = MOUSEEVENTF_LDOWN;
        SendInput(1, down, InputSize);

        Thread.Sleep(_rng.Next(10, 35));

        var up = new Input[1];
        up[0].type         = 0;
        up[0].u.mi.dwFlags = MOUSEEVENTF_LUP;
        SendInput(1, up, InputSize);
    }

    // ── Aim Loop ──────────────────────────────────────────────────────────────
    private void AimLoop()
    {
        int    lastFid = -1;
        float  accumX  = 0, accumY = 0;
        // Для точного таймера используем Stopwatch вместо Thread.Sleep(int)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double lastT = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            if (!AimHeld || !Enabled || Vision == null || !Vision.IsReady()
                || Vision.LastTarget == null)
            {
                lastFid = -1; accumX = accumY = 0; _emaActive = false;
                Thread.Sleep(4); lastT = sw.Elapsed.TotalSeconds; continue;
            }

            // ── Точный frame-sync или Hz-throttle ────────────────────────────
            if (AimHz > 0)
            {
                double minDt  = 1.0 / AimHz;
                double elapsed = sw.Elapsed.TotalSeconds - lastT;
                if (elapsed < minDt)
                {
                    // Спим чуть меньше чтобы не промахнуться мимо тика
                    double toSleep = minDt - elapsed - 0.0005;
                    if (toSleep > 0.001) Thread.Sleep((int)(toSleep * 1000));
                    continue;
                }
            }
            else
            {
                int fid = Vision.FrameId;
                if (fid == lastFid) { Thread.Sleep(1); continue; }
                lastFid = fid;
            }
            lastT = sw.Elapsed.TotalSeconds;

            if (Vision.LastTarget == null)
            { accumX = accumY = 0; _emaActive = false; continue; }

            RfUpdateFromTarget();

            var (moveX, moveY) = Vision.GetAimDelta(Strength, MaxStep);

            // Стоп-зона — сбрасываем аккумулятор
            if (MathF.Abs(moveX) < 0.01f && MathF.Abs(moveY) < 0.01f)
            { accumX = accumY = 0; _emaActive = false; continue; }

            // EMA сглаживание — только если Smooth > 0
            // При Smooth=0 — чистое пропорциональное наведение без инерции (нет маятника)
            if (Smooth > 0.01f)
            {
                if (!_emaActive) { _emaMx = moveX; _emaMy = moveY; _emaActive = true; }
                else
                {
                    _emaMx = Smooth * moveX + (1f - Smooth) * _emaMx;
                    _emaMy = Smooth * moveY + (1f - Smooth) * _emaMy;
                }
                // Сброс EMA при смене направления — убирает накопленный импульс
                if (moveX * _emaMx < 0) { _emaMx = moveX; accumX = 0; }
                if (moveY * _emaMy < 0) { _emaMy = moveY; accumY = 0; }
                moveX = _emaMx; moveY = _emaMy;
            }
            else { _emaActive = false; }

            // Jitter только при большой дистанции
            var (rawDx, rawDy) = Vision.GetDelta();
            float realDist = MathF.Sqrt(rawDx * rawDx + rawDy * rawDy);
            if (realDist > 40f)
            {
                moveX += (float)(_rng.NextDouble() * 2 - 1) * HumanJitter;
                moveY += (float)(_rng.NextDouble() * 2 - 1) * HumanJitter;
            }

            accumX += moveX; accumY += moveY;
            int sx = (int)accumX, sy = (int)accumY;
            if (sx != 0 || sy != 0) { MouseMove(sx, sy); accumX -= sx; accumY -= sy; }
        }
    }

    // ── Triggerbot Loop ───────────────────────────────────────────────────────
    private void TriggerbotLoop()
    {
        const int Hz      = 480;
        double    interval = 1.0 / Hz;
        double?   onSince  = null;
        double    delay    = 0;
        double    lastFire = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            double now = Ts();

            if (!TbEnabled || !Enabled || Vision == null || !Vision.IsReady())
            {
                if (Vision != null) Vision.IsOnTarget = false;
                onSince = null;
                Snooze(interval); continue;
            }
            if (LmbHeld || (TbAimOnly && !AimHeld))
            {
                Vision.IsOnTarget = false;
                onSince = null;
                Snooze(interval); continue;
            }

            bool onTarget     = Vision.IsCrosshairOnTarget(TbTolerance);
            Vision.IsOnTarget = onTarget;

            if (onTarget)
            {
                onSince ??= now;
                delay    = TbDelayMax > 0
                    ? _rng.NextDouble() * (TbDelayMax - TbDelayMin) + TbDelayMin : 0;
                if (now - onSince >= delay && now - lastFire > 0.010)
                {
                    MouseClick();
                    lastFire = Ts();
                    onSince  = null;
                }
            }
            else onSince = null;

            Snooze(Math.Max(0, interval - (Ts() - now)));
        }
    }

    private static double Ts() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() /
        System.Diagnostics.Stopwatch.Frequency;

    private static void Snooze(double seconds)
    { if (seconds > 0.001) Thread.Sleep((int)(seconds * 1000)); }

    // ── Rangefinder ───────────────────────────────────────────────────────────
    public void RfSetTable(IEnumerable<HachBobAI.Config.RangefinderEntry> table,
                            bool enabled, bool presetControlled = false)
    {
        RfEnabled          = enabled;
        RfPresetControlled = presetControlled;
        RfTable = table
            .Where(r => r.BboxH > 0)
            .Select(r => ((float)r.BboxH, (float)r.YOffset))
            .OrderByDescending(r => r.Item1)
            .ToList();
    }

    public float RfInterpolate(float bboxH)
    {
        if (RfTable.Count == 0) return 0;
        if (bboxH >= RfTable[0].bboxH)  return RfTable[0].yOffset;
        if (bboxH <= RfTable[^1].bboxH) return RfTable[^1].yOffset;
        for (int i = 0; i < RfTable.Count - 1; i++)
        {
            var (hi, oi) = RfTable[i]; var (lo, ol) = RfTable[i + 1];
            if (bboxH >= lo && bboxH <= hi)
                return ol + (bboxH - lo) / (hi - lo) * (oi - ol);
        }
        return 0;
    }

    private void RfUpdateFromTarget()
    {
        if (!RfEnabled || RfTable.Count == 0 || Vision?.LastTarget == null) return;
        float h = Vision.LastTarget.Y2 - Vision.LastTarget.Y1;
        if (h <= 0) return;
        RfCurrentOffset     = RfInterpolate(h);
        Vision.AimYOffsetPx = RfBaseYOffset + RfCurrentOffset;
    }

    // ── Input handling ────────────────────────────────────────────────────────
    private void OnMousePressed(object? sender, MouseHookEventArgs e)
        => HandleMouse(MapMouseButton(e.Data.Button), true);
    private void OnMouseReleased(object? sender, MouseHookEventArgs e)
        => HandleMouse(MapMouseButton(e.Data.Button), false);

    private void HandleMouse(string btn, bool pressed)
    {
        if (btn == "lmb") { LmbHeld = pressed; return; }
        if (btn == BindAim)
        {
            if (ToggleMode) { if (pressed) AimHeld = !AimHeld; }
            else AimHeld = pressed;
            return;
        }
        if (btn == BindSwitchTarget && pressed && Vision?.IsReady() == true)
            Vision.SwitchTarget();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        string k = MapKey(e.Data.KeyCode);
        if (string.IsNullOrEmpty(k)) return;
        if (k == BindAim)          { if (ToggleMode) AimHeld = !AimHeld; else AimHeld = true; return; }
        if (k == BindSwitchTarget && Vision?.IsReady() == true) { Vision.SwitchTarget(); return; }
        if (k == BindToggle)       { Enabled = !Enabled; OnToggleEnabled?.Invoke(Enabled); return; }
        if (k == BindOverlay)      { OnShowOverlay?.Invoke();     return; }
        if (k == BindHideGui)      { OnHideGui?.Invoke();         return; }
        if (k == BindDeadZone)     { OnDeadZoneToggle?.Invoke();  return; }
        if (k == BindTriggerbot)   { TbEnabled = !TbEnabled; OnTriggerbotToggle?.Invoke();  return; }
        if (k == BindRangefinder)  { RfEnabled = !RfEnabled; OnRangefinderToggle?.Invoke(); return; }
        if (k == BindExit)         { OnExit?.Invoke(); return; }
        if (PresetBinds.TryGetValue(k, out int idx)) OnPresetApply?.Invoke(idx);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (MapKey(e.Data.KeyCode) == BindAim && !ToggleMode) AimHeld = false;
    }

    private static string MapMouseButton(MouseButton btn) => btn switch
    {
        MouseButton.Button1 => "lmb", MouseButton.Button2 => "rmb",
        MouseButton.Button3 => "mmb", MouseButton.Button4 => "x1",
        MouseButton.Button5 => "x2",  _                   => "",
    };

    private static string MapKey(KeyCode code) => code switch
    {
        KeyCode.VcF1  => "f1",  KeyCode.VcF2  => "f2",  KeyCode.VcF3  => "f3",
        KeyCode.VcF4  => "f4",  KeyCode.VcF5  => "f5",  KeyCode.VcF6  => "f6",
        KeyCode.VcF7  => "f7",  KeyCode.VcF8  => "f8",  KeyCode.VcF9  => "f9",
        KeyCode.VcF10 => "f10", KeyCode.VcF11 => "f11", KeyCode.VcF12 => "f12",
        KeyCode.VcInsert    => "insert", KeyCode.VcHome     => "home",
        KeyCode.VcEnd       => "end",    KeyCode.VcDelete   => "delete",
        KeyCode.VcPageUp    => "page_up",KeyCode.VcPageDown => "page_down",
        KeyCode.VcBackslash => "\\",     KeyCode.VcR        => "r",
        KeyCode.VcV         => "v",      KeyCode.VcSpace    => "space",
        _ => code.ToString().Replace("Vc", "").ToLowerInvariant()
    };

    public void Dispose() { _cts.Cancel(); _hook?.Dispose(); }
}
