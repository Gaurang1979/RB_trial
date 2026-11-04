using System.Collections.Concurrent;

namespace VisionSystem.Studio.Sound;

/// <summary>Plays the sounds of Vision Studio (requirements 17.15 NXT-04). Sound never decides
/// anything: a missing device or a failed output is silent and never reaches the screen or the line.</summary>
public interface ISoundService
{
    /// <summary>Plays a sound if it is on. <paramref name="mouse"/> says a hover came from a mouse:
    /// a hover from touch or pen is never played.</summary>
    void Play(SoundKind kind, bool mouse = false);

    /// <summary>Reads an alarm's text aloud when spoken alarms are on (NXT-05).</summary>
    void SpeakAlarm(string text, string language);

    /// <summary>Stops what is being said, for example when the alarm is cleared.</summary>
    void StopSpeaking();
}

/// <summary>Where the samples go: the sound card (Windows), or nowhere (tests, other platforms).</summary>
public interface ISoundOutput
{
    /// <summary>Starts playing a WAV file in memory and returns at once; a new sound cuts the one playing.</summary>
    void Play(byte[] wav);
}

public sealed class NullSoundOutput : ISoundOutput
{
    public void Play(byte[] wav) { }
}

public sealed class SoundService : ISoundService
{
    private readonly ISoundOutput _output;
    private readonly ISpeech _speech;
    private readonly Func<SoundSettings> _settings;
    private readonly ConcurrentDictionary<(SoundKind, double), byte[]> _cache = new();

    public SoundService(ISoundOutput output, ISpeech speech, Func<SoundSettings> settings)
    {
        _output = output;
        _speech = speech;
        _settings = settings;
    }

    /// <summary>The sounds that could not be played since start (a broken device); shown nowhere.</summary>
    public int Failures { get; private set; }

    public void Play(SoundKind kind, bool mouse = false)
    {
        var s = _settings();
        if (!s.IsOn(kind) || s.Volume <= 0) return;
        if (kind == SoundKind.Hover && !mouse) return;
        try
        {
            var wav = _cache.GetOrAdd((kind, s.Volume), k => SoundSynth.Wav(SoundSynth.Render(k.Item1, k.Item2)));
            _output.Play(wav);
        }
        catch (Exception)
        {
            Failures++;
        }
    }

    public void SpeakAlarm(string text, string language)
    {
        var s = _settings();
        if (!s.SpokenAlarms || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _speech.Speak(text, language);
        }
        catch (Exception)
        {
            Failures++;
        }
    }

    public void StopSpeaking()
    {
        try
        {
            _speech.Cancel();
        }
        catch (Exception)
        {
            Failures++;
        }
    }

    /// <summary>Plays nothing, says nothing: headless tests and views made without the app.</summary>
    public static readonly ISoundService None = new SoundService(new NullSoundOutput(), new NullSpeech(), () => SoundSettings.Silent);
}
