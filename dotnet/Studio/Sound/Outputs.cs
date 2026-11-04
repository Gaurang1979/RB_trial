using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Globalization;

namespace VisionSystem.Studio.Sound;

/// <summary>Windows sound output: PlaySound from memory, asynchronous (winmm, in every Windows).
/// The buffer is kept while it plays; a new sound stops the one before.</summary>
public sealed class WinMmSoundOutput : ISoundOutput
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;
    private byte[]? _playing;
    private GCHandle _pin;
    private readonly object _gate = new();

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    public void Play(byte[] wav)
    {
        lock (_gate)
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);   // stops the sound playing, so its buffer may go
            if (_pin.IsAllocated) _pin.Free();
            _playing = wav;
            _pin = GCHandle.Alloc(_playing, GCHandleType.Pinned);
            PlaySound(_pin.AddrOfPinnedObject(), IntPtr.Zero, SndAsync | SndNoDefault | SndMemory);
        }
    }
}

/// <summary>Reads text aloud with the platform's voice (requirements 17.15 NXT-05).</summary>
public interface ISpeech
{
    void Speak(string text, string language);
    void Cancel();
}

public sealed class NullSpeech : ISpeech
{
    public void Speak(string text, string language) { }
    public void Cancel() { }
}

/// <summary>Windows speech (SAPI through System.Speech). It picks an installed voice of the language
/// when there is one, else the default voice; with no voice at all it says nothing.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsSpeech : ISpeech, IDisposable
{
    private SpeechSynthesizer? _synth;

    public void Speak(string text, string language)
    {
        _synth ??= new SpeechSynthesizer();
        _synth.SpeakAsyncCancelAll();
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            var voice = _synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);
            if (voice is not null) _synth.SelectVoice(voice.VoiceInfo.Name);
        }
        catch (Exception)
        {
            // an unknown language or no such voice: the default voice reads it
        }
        _synth.Rate = -1;   // a little slower than normal, as in the sketch (rate 0.92)
        _synth.SpeakAsync(text);
    }

    public void Cancel() => _synth?.SpeakAsyncCancelAll();

    public void Dispose() => _synth?.Dispose();
}

/// <summary>The sound output and voice of this platform: Windows plays and speaks; elsewhere (the Mac
/// with the stub runtime, tests) nothing is heard.</summary>
public static class SoundPlatform
{
    public static ISoundOutput Output() => OperatingSystem.IsWindows() ? new WinMmSoundOutput() : new NullSoundOutput();
    public static ISpeech Speech() => OperatingSystem.IsWindows() ? new WindowsSpeech() : new NullSpeech();
}
