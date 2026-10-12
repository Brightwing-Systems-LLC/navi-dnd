using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NaviDnD.Helpers;

// The game's music and ambient sounds. One output device and a mixer — tracks play in layers: each has its own volume and
// smooth fade-in/fade-out. What plays is decided by MusicDirector (by the hero's location, combat, time of day):
//  - changing music: Play(new) + Stop(old) — a crossfade;
//  - several tracks together (music + ambience, later combat layers): Play with different ids, volume via SetLayerVolume.
// Volume: layer × "MUSIC VOLUME" × master "VOLUME" (Sound.Volume); "MUSIC: OFF" / "AMBIENT SOUNDS: OFF" —
// silence without stopping the tracks. Formats: wav, mp3, ogg.
internal static class Music
{
    public static string SoundDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sound");

    public static bool Enabled { get; set; } = true;
    public static bool AmbienceEnabled { get; set; } = true;
    public static float Volume { get; set; } = 0.4f;   // "MUSIC VOLUME", 0..1
    // While the DM's speech plays (Speech): music and ambience duck smoothly to DuckLevel, then come back.
    public static volatile bool Ducked;
    private const float DuckLevel = 0.3f, DuckDownSec = 0.4f, DuckUpSec = 1.5f;

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private static readonly object _lock = new();
    private static IWavePlayer? _output;
    private static bool _paused;

    public static void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (_paused == paused) return;
            _paused = paused;
            if (_output == null) return;
            if (paused) _output.Pause();
            else _output.Play();
        }
    }
    private static MixingSampleProvider? _mixer;
    private static readonly Dictionary<string, Layer> _layers = [];

    // Start track id (path relative to the sound folder) with a fade-in; if it is already playing, only set the volume.
    // loop — on repeat (ambience, combat); otherwise the track plays out and goes silent by itself (IsPlaying → false).
    // ambience — an ambient sound: muted by its own setting rather than "MUSIC".
    public static void Play(string id, string file, float volume = 1f, int fadeMs = 2000, bool loop = true, bool ambience = false)
    {
        lock (_lock)
        {
            if (_layers.TryGetValue(id, out var playing))
            {
                if (!playing.Ended) { playing.FadeTo(volume, fadeMs); return; }
                _layers.Remove(id);
                playing.Dispose();
            }
            string path = Path.IsPathRooted(file) ? file : Path.Combine(SoundDir, file);
            if (!File.Exists(path) || !EnsureOutput()) return;
            try
            {
                var layer = new Layer(path, loop, ambience);
                layer.FadeTo(volume, fadeMs);
                _layers[id] = layer;
                _mixer!.AddMixerInput(layer);
            }
            catch (Exception ex) { AudioOutput.Log(ex); /* the file cannot be read — do without it */ }
        }
    }

    // The track is still sounding (has not finished and was not stopped).
    public static bool IsPlaying(string id)
    {
        lock (_lock) return _layers.TryGetValue(id, out var layer) && !layer.Ended;
    }

    // Fade out a track and remove it from the mixer.
    public static void Stop(string id, int fadeMs = 2000)
    {
        lock (_lock)
        {
            if (!_layers.Remove(id, out var layer)) return;
            if (layer.Ended) { layer.Dispose(); return; }   // finished — the mixer has already removed it
            layer.FadeTo(0, fadeMs, then: () => { lock (_lock) _mixer?.RemoveMixerInput(layer); layer.Dispose(); });
        }
    }

    // Layer volume (0..1) with a smooth transition — for mixing several tracks.
    public static void SetLayerVolume(string id, float volume, int fadeMs = 1000)
    {
        lock (_lock) if (_layers.TryGetValue(id, out var layer)) layer.FadeTo(volume, fadeMs);
    }

    private static bool EnsureOutput()
    {
        if (_output != null) return true;
        try
        {
            _mixer = new MixingSampleProvider(Format) { ReadFully = true };   // with no tracks it plays silence; the device stays open
            _output = AudioOutput.Create(200);
            _output.Init(new Ducking(_mixer));
            if (!_paused) _output.Play();
            return true;
        }
        catch (Exception ex) { AudioOutput.Log(ex); _output = null; _mixer = null; return false; }
    }

    // Ducking the whole mixer for speech: down fast, back up slowly.
    private sealed class Ducking(ISampleProvider source) : ISampleProvider
    {
        private float _gain = 1f;
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            bool ducked = Ducked;
            float target = ducked ? DuckLevel : 1f;
            float step = (1f - DuckLevel) / ((ducked ? DuckDownSec : DuckUpSec) * Format.SampleRate * Format.Channels);
            for (int i = 0; i < read; i++)
            {
                if (_gain != target) _gain = _gain > target ? MathF.Max(target, _gain - step) : MathF.Min(target, _gain + step);
                buffer[offset + i] *= _gain;
            }
            return read;
        }
    }

    // A track: a file (wav/mp3/ogg) converted to the mixer format × layer volume (with fades) × master.
    private sealed class Layer : ISampleProvider, IDisposable
    {
        private readonly WaveStream _reader;
        private readonly ISampleProvider _source;
        private readonly bool _loop, _ambience;
        private float _gain, _target, _step;
        private Action? _then;
        private volatile bool _ended;

        public WaveFormat WaveFormat => Format;
        public bool Ended => _ended;

        public Layer(string path, bool loop, bool ambience)
        {
            _loop = loop;
            _ambience = ambience;
            _reader = AudioOutput.Open(path, out var s);
            if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
            if (s.WaveFormat.SampleRate != Format.SampleRate) s = new WdlResamplingSampleProvider(s, Format.SampleRate);
            _source = s;
        }

        public void FadeTo(float volume, int ms, Action? then = null)
        {
            _target = MathF.Sqrt(Math.Clamp(volume, 0f, 1f));   // the transition follows perceived loudness; amplitude is its square
            int samples = Math.Max(1, ms * Format.SampleRate / 1000 * Format.Channels);
            _step = (_target - _gain) / samples;
            _then = then;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_ended) return 0;
            int read = 0;
            bool rewound = false;
            while (read < count)
            {
                int n = _source.Read(buffer, offset + read, count - read);
                if (n == 0)
                {
                    if (!_loop || rewound) break;   // finished / empty file — do not spin
                    _reader.Position = 0;           // loop
                    rewound = true;
                    continue;
                }
                rewound = false;
                read += n;
            }
            float master = (_ambience ? AmbienceEnabled : Enabled) ? Volume * Sound.Volume : 0f;
            for (int i = 0; i < read; i++)
            {
                if (_step != 0)
                {
                    _gain += _step;
                    if ((_step > 0 && _gain >= _target) || (_step < 0 && _gain <= _target)) { _gain = _target; _step = 0; Finish(); }
                }
                buffer[offset + i] *= _gain * _gain * master;   // squared — a smooth rise from silence, no abrupt start
            }
            if (read < count)
            {
                _ended = true;   // finished: return less than count — the mixer removes the track itself
                Finish();
            }
            return read;
        }

        private void Finish()
        {
            var then = _then;
            _then = null;
            if (then != null) Task.Run(then);   // not on the audio thread: RemoveMixerInput takes the mixer lock
        }

        public void Dispose() => _reader.Dispose();
    }
}
