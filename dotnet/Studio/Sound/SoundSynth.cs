namespace VisionSystem.Studio.Sound;

/// <summary>The sounds of Vision Studio. Each has one meaning (requirements 17.15 NXT-04).</summary>
public enum SoundKind
{
    /// <summary>A bad part: two low square beeps.</summary>
    BadPart,
    /// <summary>An alarm: 660 and 880 Hz taking turns, twice a second.</summary>
    Alarm,
    /// <summary>The line started: a rising sweep.</summary>
    Start,
    /// <summary>The line stopped: a falling sweep.</summary>
    Stop,
    /// <summary>Saved: three rising notes.</summary>
    Saved,
    /// <summary>A good part: a short tick (off by default: good parts are silent).</summary>
    GoodPart,
    /// <summary>A tap or click on a control.</summary>
    Tap,
    /// <summary>The mouse moved onto a control (mouse only, never touch).</summary>
    Hover,
}

public enum Wave { Sine, Square, Triangle, Sawtooth }

/// <summary>One note: a start time and length in seconds, a frequency (sweeping to <paramref name="ToHz"/>
/// when given) and a peak level from 0 to 1.</summary>
public readonly record struct Tone(double Hz, double Seconds, Wave Wave, double Gain, double At = 0, double? ToHz = null);

/// <summary>Makes every sound in code, as the approved sketch does with WebAudio
/// (design/prototypes/vision-studio-next/index.html): no audio files ship. Output is 16-bit mono PCM.</summary>
public static class SoundSynth
{
    public const int SampleRate = 22050;
    private const double Attack = 0.006;

    /// <summary>The notes of a sound, as in the sketch.</summary>
    public static IReadOnlyList<Tone> Tones(SoundKind kind) => kind switch
    {
        SoundKind.BadPart => [new(196, 0.12, Wave.Square, 0.5), new(196, 0.12, Wave.Square, 0.5, At: 0.16)],
        SoundKind.Alarm => Enumerable.Range(0, 4).SelectMany(i => new Tone[]
        {
            new(660, 0.2, Wave.Triangle, 0.7, At: i * 0.5),
            new(880, 0.2, Wave.Triangle, 0.7, At: i * 0.5 + 0.25),
        }).ToArray(),
        SoundKind.Start => [new(220, 0.38, Wave.Sawtooth, 0.35, ToHz: 1200)],
        SoundKind.Stop => [new(1000, 0.38, Wave.Sawtooth, 0.35, ToHz: 180)],
        SoundKind.Saved => [new(660, 0.1, Wave.Sine, 0.5), new(880, 0.1, Wave.Sine, 0.5, At: 0.09), new(1320, 0.18, Wave.Sine, 0.5, At: 0.18)],
        SoundKind.GoodPart => [new(1320, 0.05, Wave.Sine, 0.25)],
        SoundKind.Tap => [new(1400, 0.05, Wave.Triangle, 0.5, ToHz: 900)],
        SoundKind.Hover => [new(2400, 0.025, Wave.Sine, 0.12)],
        _ => [],
    };

    /// <summary>The samples of a sound at a volume from 0 to 1.</summary>
    public static short[] Render(SoundKind kind, double volume) => Render(Tones(kind), volume);

    public static short[] Render(IReadOnlyList<Tone> tones, double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        var seconds = tones.Count == 0 ? 0 : tones.Max(t => t.At + t.Seconds) + 0.02;
        var mix = new double[(int)Math.Ceiling(seconds * SampleRate)];
        foreach (var t in tones)
        {
            var start = (int)(t.At * SampleRate);
            var count = (int)(t.Seconds * SampleRate);
            var phase = 0.0;
            for (var i = 0; i < count && start + i < mix.Length; i++)
            {
                var time = (double)i / SampleRate;
                // exponential sweep, as WebAudio's exponentialRampToValueAtTime
                var hz = t.ToHz is { } to ? t.Hz * Math.Pow(to / t.Hz, time / t.Seconds) : t.Hz;
                phase += hz / SampleRate;
                phase -= Math.Floor(phase);
                // a 6 ms rise, then an exponential fall to -80 dB at the end of the note: no clicks
                var envelope = time < Attack ? time / Attack : Math.Pow(1e-4, (time - Attack) / Math.Max(t.Seconds - Attack, 1e-6));
                mix[start + i] += t.Gain * envelope * Shape(t.Wave, phase);
            }
        }
        var samples = new short[mix.Length];
        for (var i = 0; i < mix.Length; i++)
        {
            samples[i] = (short)Math.Round(Math.Clamp(mix[i] * volume, -1, 1) * 0.5 * short.MaxValue);
        }
        return samples;
    }

    private static double Shape(Wave wave, double phase) => wave switch
    {
        Wave.Square => phase < 0.5 ? 1 : -1,
        Wave.Triangle => 1 - 4 * Math.Abs(phase - 0.5),
        Wave.Sawtooth => 2 * phase - 1,
        _ => Math.Sin(2 * Math.PI * phase),
    };

    /// <summary>The samples as a WAV file in memory (RIFF, PCM 16-bit mono), for outputs that play one.</summary>
    public static byte[] Wav(short[] samples)
    {
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + samples.Length * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)1);              // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);        // bytes per second
        w.Write((short)2);              // block align
        w.Write((short)16);             // bits per sample
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return stream.ToArray();
    }
}
