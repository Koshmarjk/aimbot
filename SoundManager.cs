// Audio/SoundManager.cs — NAudio
using System.IO;
using NAudio.Wave;

namespace HachBobAI.Audio;

public sealed class SoundManager : IDisposable
{
    private readonly Dictionary<string, byte[]> _sounds = [];
    private float _volume = 0.20f;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public SoundManager()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var (key, file) in new[]
        {
            ("on",      "sounds/on.mp3"),
            ("off",     "sounds/off.mp3"),
            ("gui_on",  "sounds/gui_on.mp3"),
            ("gui_off", "sounds/gui_off.mp3"),
        })
        {
            var path = Path.Combine(baseDir, file);
            if (File.Exists(path))
            {
                try { _sounds[key] = File.ReadAllBytes(path); }
                catch (Exception ex) { Console.WriteLine($"[sound] {file}: {ex.Message}"); }
            }
        }
    }

    public void SetVolume(float percent) => _volume = Math.Clamp(percent / 100f, 0f, 1f);

    public void Play(string key, float mult = 1f)
    {
        if (_disposed) return;
        if (!_sounds.TryGetValue(key, out var data) || _volume <= 0) return;

        var ct = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                using var ms      = new MemoryStream(data);
                using var reader  = new Mp3FileReader(ms);
                using var waveOut = new WaveOutEvent();
                waveOut.Volume = Math.Clamp(_volume * mult, 0f, 1f);
                waveOut.Init(reader);
                waveOut.Play();

                var tcs = new TaskCompletionSource();
                waveOut.PlaybackStopped += (_, _) => tcs.TrySetResult();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                timeoutCts.Token.Register(() => tcs.TrySetResult());
                await tcs.Task;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[sound] Play('{key}'): {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
