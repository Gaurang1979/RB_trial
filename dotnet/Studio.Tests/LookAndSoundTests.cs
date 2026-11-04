using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.Settings;
using VisionSystem.Studio.Sound;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>Records what would have been played or said: the null device of the tests.</summary>
internal sealed class RecordingOutput : ISoundOutput, ISpeech
{
    public List<byte[]> Played { get; } = [];
    public List<string> Said { get; } = [];
    public int Cancels { get; private set; }
    public bool Broken { get; set; }

    public void Play(byte[] wav)
    {
        if (Broken) throw new IOException("no sound device");
        Played.Add(wav);
    }

    public void Speak(string text, string language)
    {
        if (Broken) throw new InvalidOperationException("no voice");
        Said.Add(text);
    }

    public void Cancel() => Cancels++;
}

/// <summary>The sounds are made in code with the sketch's notes (requirements 17.15 NXT-04).</summary>
public class SoundSynthTests
{
    private const int Rate = SoundSynth.SampleRate;

    /// <summary>The frequency of a stretch of samples from its zero crossings.</summary>
    private static double Hz(short[] s, double from, double to)
    {
        int a = (int)(from * Rate), b = (int)(to * Rate), crossings = 0;
        for (var i = a + 1; i < b; i++) if ((s[i - 1] < 0) != (s[i] < 0)) crossings++;
        return crossings / 2.0 / (to - from);
    }

    private static int Peak(short[] s, double from, double to) =>
        s.Skip((int)(from * Rate)).Take((int)((to - from) * Rate)).Select(v => Math.Abs((int)v)).DefaultIfEmpty(0).Max();

    [Fact]
    public void Bad_part_is_two_120_ms_square_beeps_at_196_Hz()
    {
        var tones = SoundSynth.Tones(SoundKind.BadPart);
        Assert.Equal(2, tones.Count);
        Assert.All(tones, t => Assert.Equal((196, 0.12, Wave.Square), (t.Hz, t.Seconds, t.Wave)));
        var s = SoundSynth.Render(SoundKind.BadPart, 1);
        Assert.InRange(Hz(s, 0.005, 0.10), 186, 206);
        Assert.InRange(Hz(s, 0.165, 0.26), 186, 206);
        Assert.Equal(0, Peak(s, 0.125, 0.155));            // the gap between the two beeps is silent
        Assert.InRange(s.Length / (double)Rate, 0.28, 0.31);
    }

    [Fact]
    public void Alarm_alternates_660_and_880_Hz_twice_a_second()
    {
        var s = SoundSynth.Render(SoundKind.Alarm, 1);
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var t = cycle * 0.5;
            Assert.InRange(Hz(s, t + 0.01, t + 0.15), 640, 680);
            Assert.InRange(Hz(s, t + 0.26, t + 0.40), 855, 905);
        }
        Assert.InRange(s.Length / (double)Rate, 1.95, 2.0);   // four 0.5 s cycles = 2 Hz
    }

    [Fact]
    public void Start_sweeps_up_and_stop_sweeps_down()
    {
        var start = SoundSynth.Render(SoundKind.Start, 1);
        Assert.True(Hz(start, 0.30, 0.36) > 2 * Hz(start, 0.01, 0.07));
        var stop = SoundSynth.Render(SoundKind.Stop, 1);
        Assert.True(Hz(stop, 0.01, 0.07) > 2 * Hz(stop, 0.30, 0.36));
    }

    [Fact]
    public void Saved_is_three_rising_notes_and_the_hover_tick_is_the_shortest_sound()
    {
        var saved = SoundSynth.Tones(SoundKind.Saved);
        Assert.Equal([660.0, 880, 1320], saved.Select(t => t.Hz));
        var lengths = Enum.GetValues<SoundKind>().ToDictionary(k => k, k => SoundSynth.Render(k, 1).Length);
        Assert.Equal(lengths.Values.Min(), lengths[SoundKind.Hover]);
        Assert.All(lengths.Values, n => Assert.True(n > 0));
    }

    [Fact]
    public void Volume_scales_the_level_and_zero_is_silent()
    {
        var loud = Peak(SoundSynth.Render(SoundKind.BadPart, 1), 0, 0.3);
        var half = Peak(SoundSynth.Render(SoundKind.BadPart, 0.5), 0, 0.3);
        Assert.InRange(half / (double)loud, 0.45, 0.55);
        Assert.All(SoundSynth.Render(SoundKind.Alarm, 0), v => Assert.Equal(0, v));
        Assert.True(loud < short.MaxValue);                  // headroom: never clipped
    }

    [Fact]
    public void Wav_is_16_bit_mono_PCM_of_the_samples()
    {
        var samples = SoundSynth.Render(SoundKind.Tap, 1);
        var wav = SoundSynth.Wav(samples);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));      // mono
        Assert.Equal(Rate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal(samples.Length * 2, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44 + samples.Length * 2, wav.Length);
    }
}

/// <summary>What plays, on a null device: good parts silent by default, hover from a mouse only, a
/// broken device or voice silent (requirements 17.15 NXT-04, NXT-05).</summary>
public class SoundServiceTests
{
    private readonly RecordingOutput _out = new();
    private SoundSettings _settings = SoundSettings.Default;

    private SoundService Service() => new(_out, _out, () => _settings);

    [Fact]
    public void Every_sound_plays_by_default_except_the_good_part_tick()
    {
        var service = Service();
        foreach (var kind in Enum.GetValues<SoundKind>()) service.Play(kind, mouse: true);
        Assert.Equal(Enum.GetValues<SoundKind>().Length - 1, _out.Played.Count);
        Assert.False(SoundSettings.Default.IsOn(SoundKind.GoodPart));
    }

    [Fact]
    public void Hover_plays_only_for_a_mouse()
    {
        var service = Service();
        service.Play(SoundKind.Hover);
        Assert.Empty(_out.Played);
        service.Play(SoundKind.Hover, mouse: true);
        Assert.Single(_out.Played);
    }

    [Fact]
    public void A_sound_turned_off_or_volume_zero_plays_nothing()
    {
        var service = Service();
        _settings = SoundSettings.Default.With(SoundKind.BadPart, false);
        service.Play(SoundKind.BadPart);
        Assert.Empty(_out.Played);
        _settings = SoundSettings.Default.With(SoundKind.GoodPart, true);
        service.Play(SoundKind.GoodPart);
        Assert.Single(_out.Played);
        _settings = SoundSettings.Default with { Volume = 0 };
        service.Play(SoundKind.Alarm);
        Assert.Single(_out.Played);
    }

    [Fact]
    public void The_volume_reaches_the_samples()
    {
        var service = Service();
        _settings = SoundSettings.Default with { Volume = 1 };
        service.Play(SoundKind.BadPart);
        _settings = SoundSettings.Default with { Volume = 0.25 };
        service.Play(SoundKind.BadPart);
        static int Peak(byte[] wav) => Enumerable.Range(0, (wav.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(wav, 44 + i * 2)));
        Assert.InRange(Peak(_out.Played[1]) / (double)Peak(_out.Played[0]), 0.2, 0.3);
    }

    [Fact]
    public void A_broken_device_or_voice_is_silent_and_never_throws()
    {
        _out.Broken = true;
        var service = Service();
        service.Play(SoundKind.Alarm);
        service.SpeakAlarm("Camera glass is dirty. Wipe it.", "en");
        Assert.Equal(2, service.Failures);
        Assert.Empty(_out.Played);
    }

    [Fact]
    public void Alarms_are_spoken_unless_turned_off()
    {
        var service = Service();
        service.SpeakAlarm("Camera glass is dirty. Wipe it.", "en");
        Assert.Equal(["Camera glass is dirty. Wipe it."], _out.Said);
        _settings = SoundSettings.Default with { SpokenAlarms = false };
        service.SpeakAlarm("Light is off.", "en");
        Assert.Single(_out.Said);
        service.StopSpeaking();
        Assert.Equal(1, _out.Cancels);
    }

    [Fact]
    public void The_none_service_is_silent()
    {
        SoundService.None.Play(SoundKind.Alarm);
        SoundService.None.SpeakAlarm("x", "en");
    }

    [Fact]
    public void The_run_screen_sounds_start_stop_alarm_and_bad_parts_from_the_runtime()
    {
        var link = new ManualLink();
        var service = Service();
        var run = new RunViewModel(Make.Strings, new RuntimeCalls(link), new NoOverlays(), new ManualTime(), _ => null) { Sound = service };
        int Count() => _out.Played.Count;
        run.Apply(LinkState.Connected, Make.Stopped());
        Assert.Equal(0, Count());                              // the first status is the state found, not a change
        run.Apply(LinkState.Connected, Make.Running());
        Assert.Equal(1, Count());                              // start sweep
        run.Apply(LinkState.Connected, Make.Running() with { Alarm = "Image is getting darker. Wipe the glass." });
        Assert.Equal(2, Count());
        Assert.Equal(["Image is getting darker. Wipe the glass."], _out.Said);
        run.Apply(LinkState.Connected, Make.Running());
        Assert.Equal(1, _out.Cancels);                         // the alarm cleared: stop speaking it
        run.OnFrame(Make.Frame(Verdict.Ng));
        Assert.Equal(3, Count());
        run.OnFrame(Make.Frame(Verdict.Ok, 2));
        Assert.Equal(3, Count());                              // a good part is silent by default
        run.Apply(LinkState.Connected, Make.Stopped());
        Assert.Equal(4, Count());                              // stop sweep
        Assert.Equal(SoundSynth.Wav(SoundSynth.Render(SoundKind.Stop, _settings.Volume)), _out.Played[^1]);
    }

    private sealed class NoOverlays : IOverlayHost
    {
        public void Show(object overlay) { }
        public void Close() { }
        public void CloseIf(object overlay) { }
    }
}

/// <summary>The PC's look-and-feel settings: defaults, saved and read back, a damaged file gives the
/// defaults (requirements 17.15 NXT-04..06).</summary>
public class UiSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vs-ui-settings-").FullName;
    private string File => System.IO.Path.Combine(_dir, "sub", "ui-settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_are_sound_on_good_parts_silent_alarms_spoken_motion_on()
    {
        var s = new UiSettingsStore(File).Load();
        Assert.Equal(UiSettings.Default, s);
        Assert.Equal(0.7, s.Sound.Volume);
        Assert.False(s.Sound.IsOn(SoundKind.GoodPart));
        Assert.True(s.Sound.IsOn(SoundKind.BadPart));
        Assert.True(s.Sound.SpokenAlarms);
        Assert.False(s.ReduceMotion);
    }

    [Fact]
    public void Settings_are_saved_and_read_back()
    {
        var store = new UiSettingsStore(File);
        var s = new UiSettings
        {
            Sound = SoundSettings.Default.With(SoundKind.GoodPart, true).With(SoundKind.Hover, false) with { Volume = 0.35, SpokenAlarms = false },
            ReduceMotion = true,
        };
        Assert.True(store.Save(s));
        var back = store.Load();
        Assert.Equal(0.35, back.Sound.Volume);
        Assert.True(back.Sound.IsOn(SoundKind.GoodPart));
        Assert.False(back.Sound.IsOn(SoundKind.Hover));
        Assert.True(back.Sound.IsOn(SoundKind.Alarm));
        Assert.False(back.Sound.SpokenAlarms);
        Assert.True(back.ReduceMotion);
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults_and_unknown_sounds_are_ignored()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.Equal(UiSettings.Default, new UiSettingsStore(File).Load());
        System.IO.File.WriteAllText(File, """{ "volume": 4, "sounds": { "bell": false, "badPart": false } }""");
        var s = new UiSettingsStore(File).Load();
        Assert.Equal(1, s.Sound.Volume);                        // clamped
        Assert.False(s.Sound.IsOn(SoundKind.BadPart));
        Assert.False(s.Sound.IsOn(SoundKind.GoodPart));         // not in the file: its default
    }

    [Fact]
    public void The_display_panel_changes_and_saves_the_settings()
    {
        var prefs = new UiPreferences(new UiSettingsStore(File));
        var changed = 0;
        prefs.Changed += () => changed++;
        var vm = new DisplayOptionsViewModel(Make.Strings, AppTheme.Dark, _ => { }, () => { }, prefs);
        Assert.Equal(70, vm.Volume);
        Assert.Equal(DisplayOptionsViewModel.SoundRows.Length, vm.Sounds.Count);
        Assert.False(vm.Sounds.Single(t => t.Kind == SoundKind.GoodPart).On);
        vm.Sounds.Single(t => t.Kind == SoundKind.GoodPart).On = true;
        vm.Volume = 40;
        vm.ReduceMotion = true;
        vm.SpokenAlarms = false;
        Assert.Equal(4, changed);
        var saved = new UiSettingsStore(File).Load();
        Assert.True(saved.Sound.IsOn(SoundKind.GoodPart));
        Assert.Equal(0.4, saved.Sound.Volume, 3);
        Assert.True(saved.ReduceMotion);
        Assert.False(saved.Sound.SpokenAlarms);
    }
}

/// <summary>Reduce motion: the setting or the OS; and the window class the styles read (NXT-06).</summary>
public class MotionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Motion_is_reduced_by_the_setting_or_the_operating_system(bool setting, bool os, bool reduced) =>
        Assert.Equal(reduced, Motion.Reduced(setting, () => os));

    [Fact]
    public void Flashing_is_at_most_2_Hz_and_animations_under_300_ms()
    {
        Assert.Equal(2, Motion.MaxFlashHz);
        Assert.Equal(TimeSpan.FromMilliseconds(300), Motion.MaxAnimation);
        // the alarm sound keeps the same pace as the flashing it goes with
        var alarm = SoundSynth.Tones(SoundKind.Alarm);
        Assert.Equal(0.5, alarm[2].At - alarm[0].At, 6);
    }

    [AvaloniaFact]
    public void The_window_gets_the_reduce_motion_class()
    {
        var window = new Window();
        App.ApplyMotion(window, UiSettings.Default, () => false);
        Assert.DoesNotContain(Motion.ReduceClass, window.Classes);
        App.ApplyMotion(window, UiSettings.Default with { ReduceMotion = true }, () => false);
        Assert.Contains(Motion.ReduceClass, window.Classes);
        App.ApplyMotion(window, UiSettings.Default, () => true);
        Assert.Contains(Motion.ReduceClass, window.Classes);
    }
}

/// <summary>The instrument colour, the font roles and the button states (NXT-01..03).</summary>
public class LookTests
{
    private static readonly string Root = Make.RepoRoot();
    private static readonly JsonElement Tokens = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(Root, "design/tokens/vision-tokens.json"))).RootElement;

    private static string Colour(string path, string theme)
    {
        var node = Tokens.GetProperty("color");
        foreach (var part in path.Split('.')) node = node.GetProperty(part);
        return node.GetProperty(theme).GetString()!;
    }

    private static double Luminance(string hex)
    {
        var rgb = hex.TrimStart('#');
        if (rgb.Length == 8) rgb = rgb[2..];
        double Channel(int i)
        {
            var c = int.Parse(rgb.Substring(i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData("dark", 3.0)]
    [InlineData("light", 3.0)]
    [InlineData("highContrast", 7.0)]
    public void Hud_shows_on_every_background_of_each_theme(string theme, double minimum)
    {
        foreach (var bg in new[] { "bg", "surface", "surfaceRaised" })
        {
            Assert.True(Contrast(Colour("hud", theme), Colour(bg, theme)) >= minimum, $"hud on {bg} in {theme}");
        }
        Assert.Equal(9, Colour("hudDim", theme).Length);       // the dim variant is see-through (#AARRGGBB)
        Assert.EndsWith(Colour("hud", theme)[1..], Colour("hudDim", theme));
    }

    [Fact]
    public void Hud_is_never_a_status_colour()
    {
        foreach (var theme in new[] { "dark", "light", "highContrast" })
        {
            foreach (var status in Tokens.GetProperty("color").GetProperty("status").EnumerateObject())
            {
                Assert.NotEqual(Colour("hud", theme), status.Value.GetProperty(theme).GetString(), StringComparer.OrdinalIgnoreCase);
            }
            Assert.NotEqual(Colour("hud", theme), Colour("accent", theme), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Typefaces_are_generated_with_the_embedded_fonts_and_their_licences()
    {
        var xaml = XDocument.Load(System.IO.Path.Combine(Root, "dotnet/Studio/Theme/Tokens.g.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var families = xaml.Root!.Elements().Where(e => e.Name.LocalName == "FontFamily")
            .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Value);
        var css = System.IO.File.ReadAllText(System.IO.Path.Combine(Root, "dotnet/Runtime.Host/Web/tokens.g.css"));
        var fonts = System.IO.Path.Combine(Root, "dotnet/Studio/Assets/Fonts");
        foreach (var (role, file) in new[] { ("display", "Rajdhani"), ("body", "NotoSans"), ("mono", "JetBrainsMono") })
        {
            var face = Tokens.GetProperty("typeface").GetProperty(role);
            var family = face.GetProperty("family").GetString()!;
            var key = "Vs.Typeface." + char.ToUpperInvariant(role[0]) + role[1..];
            Assert.StartsWith($"avares://vs-studio/Assets/Fonts#{family}, ", families[key]);
            Assert.EndsWith("$Default", families[key]);
            Assert.Contains($"--vs-typeface-{role}: \"{family}\"", css);
            Assert.NotEmpty(Directory.GetFiles(fonts, file + "-*.ttf"));
            Assert.Contains("SIL Open Font License", System.IO.File.ReadAllText(System.IO.Path.Combine(fonts, file + "-OFL.txt")));
        }
        Assert.Equal(families["Vs.Typeface.Body"], families["ContentControlThemeFontFamily"]);
    }

    [AvaloniaTheory]
    [InlineData("Rajdhani")]
    [InlineData("Noto Sans")]
    [InlineData("JetBrains Mono")]
    public void The_embedded_fonts_load(string family)
    {
        var typeface = new Typeface(new FontFamily($"avares://vs-studio/Assets/Fonts#{family}"), FontStyle.Normal, FontWeight.SemiBold);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs), family);
        Assert.StartsWith(family, glyphs!.FamilyName);   // a static face names its weight too ("Rajdhani SemiBold")
    }

    [AvaloniaFact]
    public void Mono_digits_are_all_one_width()
    {
        var typeface = new Typeface(new FontFamily("avares://vs-studio/Assets/Fonts#JetBrains Mono"));
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
        var widths = "0123456789".Select(c => glyphs!.GetGlyphAdvance(glyphs.GetGlyph(c))).Distinct();
        Assert.Single(widths);
    }

    private static (Window Window, Button Button) Show(bool reduceMotion, bool enabled = true)
    {
        var button = new Button { Classes = { "vs" }, Content = "Start", Width = 120, Height = 40, IsEnabled = enabled };
        ToolTip.SetTip(button, "Press Stop first.");
        var window = new Window { Width = 300, Height = 200, Content = new Panel { Children = { button } } };
        if (reduceMotion) window.Classes.Add(Motion.ReduceClass);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();   // hit testing needs a rendered frame
        Dispatcher.UIThread.RunJobs();
        return (window, button);
    }

    private static IEnumerable<Avalonia.Controls.Shapes.Path> Brackets(Button b) => b.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.Classes.Contains("hudBracket"));

    [AvaloniaFact]
    public void Hover_locks_corner_brackets_on_in_the_hud_colour()
    {
        var (window, button) = Show(reduceMotion: true);
        Assert.Equal(2, Brackets(button).Count());
        Assert.All(Brackets(button), p => Assert.Equal(0, p.Opacity));
        Assert.All(Brackets(button), p => Assert.False(p.IsHitTestVisible));   // never in the way of a tap
        window.MouseMove(new Point(60, 100));
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsPointerOver, $"hit {window.InputHitTest(new Point(60, 100))} bounds {button.Bounds}");
        Assert.All(Brackets(button), p => Assert.Equal(1, p.Opacity));
        var hud = (ISolidColorBrush)Application.Current!.FindResource(Application.Current!.ActualThemeVariant, "Vs.Brush.Hud")!;
        Assert.All(Brackets(button), p => Assert.Equal(hud.Color, ((ISolidColorBrush)p.Stroke!).Color));
        // reduce motion: no glow at all
        Assert.False(button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Glow").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Pressed_moves_down_1_px_and_a_click_still_works_without_hover()
    {
        var (window, button) = Show(reduceMotion: false);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        // a keyboard press: no pointer, no hover
        button.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
        Assert.Equal(1, clicks);
        button.Transitions = null;   // the style's end value, not the theme's 75 ms way there
        window.MouseDown(new Point(60, 100), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsPressed);
        var m = button.RenderTransform!.Value;
        Assert.Equal((0.0, 1.0, 1.0, 1.0), (m.M31, m.M32, m.M11, m.M22));   // down 1 px, no scaling
        window.MouseUp(new Point(60, 100), MouseButton.Left);
        Assert.Equal(2, clicks);
        window.Close();
    }

    [AvaloniaFact]
    public void Disabled_stays_faded_and_keeps_its_reason()
    {
        var (window, button) = Show(reduceMotion: true, enabled: false);
        Assert.Equal(0.45, button.Opacity, 3);
        Assert.True(ToolTip.GetShowOnDisabled(button));
        Assert.Equal("Press Stop first.", ToolTip.GetTip(button));
        window.MouseMove(new Point(60, 100));
        Dispatcher.UIThread.RunJobs();
        Assert.All(Brackets(button), p => Assert.Equal(0, p.Opacity));
        window.Close();
    }

    [AvaloniaFact]
    public void Buttons_use_the_display_face_and_windows_the_body_face()
    {
        var (window, button) = Show(reduceMotion: true);
        Assert.Contains("Rajdhani", button.FontFamily.ToString());
        Assert.Contains("Noto Sans", window.FontFamily.ToString());
        window.Close();
    }
}

/// <summary>The Display panel with the new Sound and motion settings fits 1366 x 768 in every theme.</summary>
public class DisplaySoundLayoutTests
{
    [AvaloniaTheory]
    [InlineData(AppTheme.Dark, "17-display-sound-dark")]
    [InlineData(AppTheme.Light, "17-display-sound-light")]
    [InlineData(AppTheme.HighContrast, "17-display-sound-high-contrast")]
    public void Sound_and_motion_settings_fit_1366_by_768(AppTheme theme, string snapshot)
    {
        Application.Current!.RequestedThemeVariant = App.VariantFor(theme);
        var vm = new MainViewModel(Make.Strings, new ManualLink(), new ImmediateUi(), TimeProvider.System, App.Decode, theme,
            t => Application.Current!.RequestedThemeVariant = App.VariantFor(t));
        var window = new Views.MainWindow { DataContext = vm, Width = 1366, Height = 768 };
        window.Show();
        vm.DisplayCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var screen = new Rect(0, 0, 1366, 768);
        foreach (var name in new[] { "SoundOptions", "Volume", "SpokenAlarms", "ReduceMotion" })
        {
            var c = window.FindDescendant<Control>(name);
            var p = c.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(screen.Contains(new Rect(p, c.Bounds.Size)), $"{name} at {p} {c.Bounds.Size} is outside 1366 x 768");
            Assert.True(c.Bounds.Height >= 24, name);
        }
        Assert.All(window.GetVisualDescendants().OfType<CheckBox>(), b => Assert.True(b.Bounds.Height >= 32, $"{b.Content} is {b.Bounds.Height} px high"));
        if (Environment.GetEnvironmentVariable("VS_SNAPSHOT_DIR") is { } dir)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, snapshot + ".png"));
        }
        window.Close();
    }
}
