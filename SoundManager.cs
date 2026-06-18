// Audio/SoundManager.cs — NAudio
using System.IO;
using NAudio.Wave;

namespace HachBobAI.Audio;

public sealed class SoundManager : IDisposable
{
    private readonly Dictionary<string, byte[]> _sounds = [];
    private float _volume = 0.20f;   // 0..1

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
                try { _sounds[key] = File.ReadAllBytes(path); }
                catch (Exception ex) { Console.WriteLine($"[sound] {file}: {ex.Message}"); }
        }
    }

    public void SetVolume(float percent) => _volume = Math.Clamp(percent / 100f, 0f, 1f);

    public void Play(string key, float mult = 1f)
    {
        if (!_sounds.TryGetValue(key, out var data) || _volume <= 0) return;
        // Fire-and-forget on thread pool
        Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(data);
                using var reader = new Mp3FileReader(ms);
                using var wave = new WaveOutEvent();
                wave.Volume = Math.Clamp(_volume * mult, 0f, 1f);
                wave.Init(reader);
                wave.Play();
                while (wave.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(20);
            }
            catch { }
        });
    }

    public void Dispose() { }
}
