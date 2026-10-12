using NAudio.Wave;

namespace NaviDnD.Helpers;

internal static class Sound
{
    // Set once from AppConfig.SoundEnabled at game start (Program.cs).
    public static bool Enabled { get; set; } = true;
    public static volatile bool Suspended;

    // Master volume 0..1 (AppConfig.SoundVolume, the "SETTINGS" screen) — a multiplier for all sounds.
    public static float Volume { get; set; } = 1f;

    private static readonly OneShotSound Click = new("click.wav");

    public static void PlayClick()
    {
        if (Enabled && !Suspended) Click.Trigger();
    }

    // Set from AppConfig.TypingSoundEnabled (Program.cs).
    public static bool TypingEnabled { get; set; } = true;

    private static readonly TypingBlipProvider? Typing = TypingBlipProvider.Create();
    private const int TypingMinIntervalMs = 55;
    private static DateTime _lastTypingBlip = DateTime.MinValue;

    // Dialogue typing sound (as in RPGs): a blip only on letters/digits and no more than once per
    // TypingMinIntervalMs — one per character at ~40 chars/s sounds like a rattle, not a "voice".
    public static void PlayTyping(char c)
    {
        if (!Enabled || Suspended || !TypingEnabled || !char.IsLetterOrDigit(c)) return;
        var now = DateTime.UtcNow;
        if ((now - _lastTypingBlip).TotalMilliseconds < TypingMinIntervalMs) return;
        _lastTypingBlip = now;
        Typing?.Trigger();
    }

    private static readonly string DiceRollPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sound", "dice-roll.mp3");

    // No preloading into memory — play the mp3 straight from disk with a fresh reader on every
    // roll. Preloading (reading the whole file into a flat array up front) consistently corrupted
    // this particular compressed clip; rolls are rare and less latency-sensitive than clicks, so
    // opening the device each time is an acceptable price for correct sound.
    public static Task PlayDiceRoll()
    {
        if (!Enabled || Suspended || !File.Exists(DiceRollPath)) return Task.CompletedTask;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Run(() =>
        {
            try
            {
                using var reader = AudioOutput.OpenMp3(DiceRollPath);
                using var output = AudioOutput.Create();
                var volume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(reader.ToSampleProvider())
                    { Volume = Suspended ? 0 : Volume };
                output.Init(volume);
                output.Play();
                started.TrySetResult();
                while (output.PlaybackState == PlaybackState.Playing)
                {
                    volume.Volume = Suspended ? 0 : Volume;
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex) { AudioOutput.Log(ex); /* sound is not a critical feature */ }
            finally { started.TrySetResult(); }
        });
        return started.Task;
    }

    // The device is opened ONCE at game start and plays continuously (silence when no sound is
    // playing) — otherwise every Play() reopens the audio device, which causes a noticeable delay.
    // The clip is decoded into a flat sample array up front, whole, once — for the click (a genuine PCM
    // wav) this works reliably.
    private sealed class OneShotSound
    {
        private readonly OneShotSampleProvider? _provider;
        private readonly IWavePlayer? _output;

        public OneShotSound(string fileName)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sound", fileName);
                if (!File.Exists(path)) return;

                using var reader = AudioOutput.Open(path, out var source);
                var samples = new List<float>();
                var buffer = new float[source.WaveFormat.SampleRate * source.WaveFormat.Channels];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    samples.AddRange(buffer.Take(read));

                _provider = new OneShotSampleProvider(samples.ToArray(), source.WaveFormat);
                _output = AudioOutput.Create(40);
                _output.Init(_provider);
                _output.Play();
            }
            catch (Exception ex) { AudioOutput.Log(ex); /* sound is not a critical feature */ }
        }

        public void Trigger() => _provider?.Trigger();
    }

    // Synthesised typing sound (no file): a quiet decaying pure sine — laptop speakers
    // rasp on lows and overtones, so it has none. Each note is random from D minor (D4–C5),
    // so text sounds like a quiet minor arpeggio rather than a metronome.
    // Like the click, the device stays open and plays silence between sounds.
    private sealed class TypingBlipProvider : ISampleProvider
    {
        private const int SampleRate = 44100;
        private const double DurationSec = 0.08;
        private const double DecaySec = 0.025;
        private const float Volume = 0.04f;
        private static readonly double[] Notes = [293.66, 349.23, 392.00, 440.00, 523.25]; // D4 F4 G4 A4 C5

        private readonly object _lock = new();
        private readonly Random _rnd = new();
        private int _pos = -1; // -1 = silence
        private double _freq = Notes[0];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        public static TypingBlipProvider? Create()
        {
            try
            {
                var provider = new TypingBlipProvider();
                var output = AudioOutput.Create(40);
                output.Init(provider);
                output.Play();
                return provider;
            }
            catch (Exception ex) { AudioOutput.Log(ex); return null; /* sound is not a critical feature */ }
        }

        public void Trigger()
        {
            lock (_lock)
            {
                _freq = Notes[_rnd.Next(Notes.Length)];
                _pos = 0;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int length = (int)(SampleRate * DurationSec);
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_pos < 0 || _pos >= length)
                    {
                        buffer[offset + i] = 0f;
                        _pos = -1;
                        continue;
                    }
                    double t = (double)_pos / SampleRate;
                    double attack = Math.Min(1.0, t / 0.005);
                    double fadeOut = Math.Min(1.0, (length - _pos) / (SampleRate * 0.01)); // no click at the end
                    buffer[offset + i] = Suspended ? 0 : (float)(Math.Sin(2 * Math.PI * _freq * t) * attack * Math.Exp(-t / DecaySec) * fadeOut * Volume * Sound.Volume);
                    _pos++;
                }
            }
            return count;
        }
    }

    private sealed class OneShotSampleProvider(float[] clip, WaveFormat format) : ISampleProvider
    {
        private int _pos = -1; // -1 = silence
        private readonly object _lock = new();

        public WaveFormat WaveFormat { get; } = format;

        public void Trigger() { lock (_lock) _pos = 0; }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_pos >= 0 && _pos < clip.Length)
                        buffer[offset + i] = clip[_pos++] * (Suspended ? 0 : Volume);
                    else
                    {
                        buffer[offset + i] = 0f;
                        _pos = -1;
                    }
                }
            }
            return count;
        }
    }
}
