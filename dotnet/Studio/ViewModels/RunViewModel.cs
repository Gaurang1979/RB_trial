using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.Sound;

namespace VisionSystem.Studio.ViewModels;

/// <summary>Opens and closes the full-screen panels over the content (sign-in, pickers, questions).</summary>
public interface IOverlayHost
{
    void Show(object overlay);
    void Close();
    /// <summary>Closes this panel if it is still the one showing (an await may have outlived it).</summary>
    void CloseIf(object overlay);
}

/// <summary>One of the last bad parts: its live picture and result, kept for the NG image viewer.</summary>
public sealed record NgThumb(IImage? Image, string Time, Tone Tone, string Icon, LiveFrame Frame);

/// <summary>One value of the batch on the Batch and PLC card; Missing while not entered.</summary>
public sealed record BatchLine(string Label, string Value, bool Missing);

/// <summary>
/// The Run screen (UI-UX-SPEC.md 4.1): live image with result graphics, the big OK / NG block,
/// counters, the last bad parts, the banner and Start, Stop, Change product, Help. Nothing else
/// on it changes the line. Every changing call is refused or allowed by the runtime.
/// </summary>
public sealed partial class RunViewModel : ObservableObject
{
    public const int StripLength = 20;
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TooltipFor = TimeSpan.FromSeconds(5);
    /// <summary>GetCounters is asked this often while connected (yield ring and cycle time).</summary>
    public static readonly TimeSpan CountersEvery = TimeSpan.FromSeconds(2);
    /// <summary>The Overview charts are worked out again at most this often, when parts came in.</summary>
    public static readonly TimeSpan OverviewEvery = TimeSpan.FromSeconds(1);
    /// <summary>With two or more cameras, a tile whose camera sent nothing for this long while
    /// another camera's pictures came is stale: dimmed, never green.</summary>
    public static readonly TimeSpan TileStaleAfter = ResultHold.LastFor;

    private readonly Strings _strings;
    private readonly RuntimeCalls _calls;
    private readonly IOverlayHost _overlays;
    private readonly TimeProvider _time;
    private readonly Func<byte[], IImage?> _decode;
    private readonly ResultHold _hold = new();
    private LinkState _link = LinkState.Connecting;
    /// <summary>The status came on the current connection (not one from before a drop).</summary>
    private bool _statusFresh;
    private RuntimeStatus? _status;
    private string? _noticeKey;
    private string? _refusalText;
    private object[] _noticeArgs = [];
    private DateTimeOffset _noticeUntil;
    private RunCounters? _runCounters;
    private DateTimeOffset _countersDue;
    private bool _countersAsked;
    private string? _bannerHelp;
    private DateTimeOffset? _plcLastTime;
    private DateTimeOffset _plcLastSeen;
    private int _overviewVersion = -1;
    private DateTimeOffset _overviewDue;
    private int _overviewMinute = -1;
    /// <summary>Product pictures by id, kept while the app runs: Change product opens at once the next time.</summary>
    private readonly Dictionary<string, IImage?> _pictures = [];

    public RunViewModel(Strings strings, RuntimeCalls calls, IOverlayHost overlays, TimeProvider time,
        Func<byte[], IImage?> decode)
    {
        _strings = strings;
        _calls = calls;
        _overlays = overlays;
        _time = time;
        _decode = decode;
        Tiles.Add(new CameraTile("", 0));
        Refresh();
    }

    /// <summary>One live image tile per camera of the job, side by side in job order (UI-UX-SPEC.md 4.1);
    /// one tile while the runtime names no camera.</summary>
    public ObservableCollection<CameraTile> Tiles { get; } = [];
    /// <summary>Every tile is dimmed (stopped, or no current picture): "This picture" is dimmed too.</summary>
    [ObservableProperty] private bool _imageDimmed = true;

    [ObservableProperty] private Tone _resultTone = Tone.Stopped;
    [ObservableProperty] private string _resultIcon = Icons.Wait;
    [ObservableProperty] private string _resultText = "";

    [ObservableProperty] private string _goodText = "0";
    [ObservableProperty] private string _badText = "0";
    [ObservableProperty] private string _totalText = "0";
    [ObservableProperty] private string _goodPercentText = "–";
    /// <summary>Yield of the batch, 0 to 100, for the ring; -1 when not known.</summary>
    [ObservableProperty] private double _yieldValue = -1;
    [ObservableProperty] private string _cycleText = "–";
    [ObservableProperty] private string _cycleMaxText = "";

    /// <summary>The Overview charts and the good % trend (UI-UX-SPEC.md 4.1, principle 15).</summary>
    [ObservableProperty] private OverviewData _overview = OverviewData.Empty;
    [ObservableProperty] private string _cycleNote = "";
    /// <summary>The tab under the image: 0 Overview, 1 Last bad parts.</summary>
    [ObservableProperty] private int _bottomTab;
    /// <summary>The line speed (parts per minute) and cycle budget (ms) from the runtime, the charts'
    /// limit lines; 0 when not known.</summary>
    [ObservableProperty] private double _lineSpeedPpm;
    [ObservableProperty] private double _cycleBudgetMs;

    /// <summary>The Batch and PLC card: the batch values, the PLC's signals and its last part.</summary>
    public ObservableCollection<BatchLine> BatchLines { get; } = [];
    [ObservableProperty] private string _batchNote = "";
    public ObservableCollection<SignalLine> PlcLines { get; } = [];
    [ObservableProperty] private string _plcNote = "";
    [ObservableProperty] private bool _hasPlcLast;
    [ObservableProperty] private Tone _plcLastTone = Tone.Neutral;
    [ObservableProperty] private string _plcLastIcon = Icons.Info;
    [ObservableProperty] private string _plcLastText = "";

    [ObservableProperty] private bool _hasBanner;
    [ObservableProperty] private Tone _bannerTone = Tone.Neutral;
    [ObservableProperty] private string _bannerIcon = Icons.Info;
    [ObservableProperty] private string _bannerText = "";
    /// <summary>The banner's alarm has "What do I do?" steps.</summary>
    [ObservableProperty] private bool _hasBannerHelp;

    [ObservableProperty] private bool _hasNotice;
    [ObservableProperty] private string _noticeText = "";

    [ObservableProperty] private bool _startAvailable;
    [ObservableProperty] private bool _stopAvailable;
    [ObservableProperty] private bool _changeAvailable;
    [ObservableProperty] private bool _isRunning;

    public ObservableCollection<NgThumb> NgStrip { get; } = [];

    /// <summary>The setup checks at job load (requirements AS-13, AS-14): one or two pictures per camera taken after
    /// the job was loaded, with the lens, light and pixel advice, shown while the line is stopped. Advice only: Start
    /// stays as it is.</summary>
    public ObservableCollection<CheckLine> SetupLines { get; } = [];
    [ObservableProperty] private bool _hasSetupCheck;
    private SetupCheckOutcome? _setupShown;
    private bool _setupRunning, _setupStale = true;

    /// <summary>The tools and codes of the picture shown, ✓ or ✗ in the runtime's words (UI-UX-SPEC.md 4.1):
    /// failed tools first, then passed ones, then codes, at most <see cref="LastPartLines"/> lines with
    /// "+N more" when there are more. With two or more cameras each camera's picture gets a heading line
    /// and its share of the lines. Cleared with every new picture and whenever the picture is stale.</summary>
    public ObservableCollection<CheckLine> LastPart { get; } = [];
    public const int LastPartLines = 6;

    /// <summary>Whether the signed-in user may review bad parts (Supervisor, QA, Engineer, Admin); set by the main window.</summary>
    public Func<bool>? MayReview { get; set; }

    /// <summary>Whether the signed-in user may send PLC test signals (Engineer, Admin: WhoAmI's
    /// may_test_plc); set by the main window. It only shows the buttons: the runtime checks the call.</summary>
    public Func<bool>? MayTestPlc { get; set; }

    /// <summary>The signed-in user changed: the PLC test buttons follow.</summary>
    public void UserChanged() => Refresh();

    /// <summary>Every part's result since the app started (the Parts list).</summary>
    public PartLog Parts { get; } = new();

    /// <summary>The runtime said nobody is signed in (or no longer knows the session).</summary>
    public event Action? SignInNeeded;

    /// <summary>Why Start would be refused now, or null when it may be pressed.</summary>
    public string? StartReasonKey { get; private set; }
    public string? StopReasonKey { get; private set; }
    public string? ChangeReasonKey { get; private set; }

    /// <summary>The sounds of the line (requirements 17.15 NXT-04, NXT-05); none in tests unless set.</summary>
    public ISoundService Sound { get; set; } = SoundService.None;

    private RuntimeStatus? _heard;

    /// <summary>Start and stop sweeps, the alarm sound and its spoken text, when the runtime says the line
    /// changed. Only what the runtime reports is heard; a sound never decides anything.</summary>
    private void Cue(LinkState link, RuntimeStatus? status)
    {
        var before = _heard;
        _heard = link == LinkState.Connected ? status : null;
        if (before is null || _heard is null) return;
        if (before.State != LineState.Running && _heard.State == LineState.Running) Sound.Play(SoundKind.Start);
        else if (before.State == LineState.Running && _heard.State != LineState.Running) Sound.Play(SoundKind.Stop);
        if (before.Alarm is null && _heard.Alarm is { } alarm)
        {
            Sound.Play(SoundKind.Alarm);
            Sound.SpeakAlarm(alarm.Length > 0 ? alarm : _strings["banner.alarm"], _strings.Language);
        }
        else if (before.Alarm is not null && _heard.Alarm is null) Sound.StopSpeaking();
    }

    public void Apply(LinkState link, RuntimeStatus? status)
    {
        Cue(link, status);
        if (link != _link)
        {
            // A new or lost connection: what was shown came from before it.
            _statusFresh = false;
            if (link != LinkState.Connected) Forget();   // never an old result as current while not reachable
        }
        if (link == LinkState.Connected && status is not null && _statusFresh && _status is { } before)
        {
            // A new run, another product or an alarm coming or going: the last part's result
            // belongs to the old situation, so it is not shown as current.
            if (status.State != before.State || status.Product != before.Product || (status.Alarm is null) != (before.Alarm is null))
            {
                Forget();
            }
            // Frames are dropped when the app cannot keep up; the runtime's count is not. A bad part
            // whose frame never came still shows NG.
            var now = _time.GetUtcNow();
            if (status.State == LineState.Running && status.Counters.Bad > before.Counters.Bad && !_hold.BadSince(now - ResultHold.BadHold))
            {
                _hold.Add(Verdict.Ng, now);
            }
        }
        if (link != _link)
        {
            // Counters of another connection may be of another batch: ask again at once.
            _runCounters = null;
            _countersDue = _time.GetUtcNow();
        }
        _link = link;
        if (link == LinkState.Connected && status is not null)
        {
            _status = status;
            _statusFresh = true;
            SetCameras(status.Cameras);
        }
        Refresh();
    }

    /// <summary>One tile per camera of the job; new tiles start without a picture.</summary>
    private void SetCameras(IReadOnlyList<string> cameras)
    {
        IReadOnlyList<string> names = cameras.Count == 0 ? [""] : cameras;
        if (Tiles.Select(t => t.Name).SequenceEqual(names)) return;
        Tiles.Clear();
        for (var i = 0; i < names.Count; i++) Tiles.Add(new CameraTile(names[i], i));
    }

    /// <summary>The tile a camera's picture goes to: its own; with one tile, or a frame that names no
    /// camera, the first. A camera the job does not list has none (its result still counts).</summary>
    private CameraTile? TileFor(string camera) =>
        Tiles.FirstOrDefault(t => t.Name == camera) ?? (Tiles.Count == 1 || camera.Length == 0 ? Tiles[0] : null);

    /// <summary>The "This picture" lines of one part: failed tools first, then passed ones, then codes,
    /// at most <paramref name="max"/> lines, the last one "+N more" when there are more.</summary>
    private List<CheckLine> Lines(LiveFrame frame, int max)
    {
        var tools = frame.Tools ?? [];
        var lines = tools.Where(t => !t.Pass).Concat(tools.Where(t => t.Pass))
            .Select(t => new CheckLine(ToolMark.Tone(t.Pass, t.ReportOnly), ToolMark.Icon(t.Pass, t.ReportOnly), t.PrintQuality is { } q
                ? _strings.Format("run.last_part.quality", t.Name, q.ToString("0", CultureInfo.CurrentCulture)) : t.Name))
            .Concat((frame.Codes ?? []).Select(c => new CheckLine(Tone.Neutral, Icons.Info, c.AddOn.Length > 0 ? $"{c.Text} +{c.AddOn}" : c.Text)))
            .Take(max + 1).ToList();
        var total = tools.Count + (frame.Codes?.Count ?? 0);
        if (total > max)
        {
            lines = lines.Take(max - 1).ToList();
            lines.Add(new CheckLine(Tone.Neutral, Icons.Info, _strings.Format("run.last_part.more", total - (max - 1))));
        }
        return lines;
    }

    /// <summary>"This picture": one camera's lines as before; with two or more, each camera's heading
    /// (its name and result, as its tile shows them) and its share of the lines. A dimmed tile's part is
    /// not current, so it lists nothing.</summary>
    private void UpdateLastPart()
    {
        var lines = new List<CheckLine>();
        if (Tiles.Count == 1)
        {
            if (Tiles[0].Frame is { } frame) lines.AddRange(Lines(frame, LastPartLines));
        }
        else
        {
            var each = Math.Max(2, (LastPartLines - Tiles.Count) / Tiles.Count);
            foreach (var tile in Tiles)
            {
                lines.Add(new CheckLine(tile.FrameTone, tile.ResultIcon, tile.Name, Header: true));
                if (tile.Frame is { } frame && !tile.Dimmed) lines.AddRange(Lines(frame, each));
            }
        }
        if (lines.SequenceEqual(LastPart)) return;
        LastPart.Clear();
        foreach (var line in lines) LastPart.Add(line);
    }

    private void Forget()
    {
        _hold.Clear();
        foreach (var tile in Tiles) tile.Forget();
        UpdateLastPart();
    }

    public void OnFrame(LiveFrame frame)
    {
        // Every camera's result counts: a bad part on any camera turns the block NG and joins the
        // bad parts; each picture goes to its own camera's tile.
        var now = _time.GetUtcNow();
        var image = _decode(frame.Jpeg);
        _hold.Add(frame.Result, now);
        if (TileFor(frame.Camera) is { } tile)
        {
            tile.LiveImage = image;
            tile.ImageWidth = Math.Max(1, frame.Width);
            tile.ImageHeight = Math.Max(1, frame.Height);
            tile.Graphics = frame.Graphics;
            tile.Frame = frame;
            tile.ShowNoPicture = image is null && frame.Graphics.Count == 0;
            tile.Stale = false;
            tile.FrameAt = now;
            tile.Hold.Add(frame.Result, now);
            if (tile.TooltipText is not null) ShowTooltipAt(tile, tile.TapAt, tile.TapSlop, keepUntil: true);   // the same spot on the new part
        }
        if (frame.Result is Verdict.Ng or Verdict.Duplicate)
        {
            AddNg(image, frame);
            Sound.Play(SoundKind.BadPart);
        }
        else if (frame.Result == Verdict.Ok) Sound.Play(SoundKind.GoodPart);
        Refresh();
    }

    private void AddNg(IImage? image, LiveFrame frame)
    {
        var time = frame.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // with two or more cameras the thumbnail says which camera saw the bad part: C1, C2 as the tiles are ordered
        if (Tiles.Count > 1 && Tiles.FirstOrDefault(t => t.Name == frame.Camera) is { } at) time = $"C{at.Index + 1} {time}";
        NgStrip.Insert(0, new NgThumb(image, time, Tones.For(frame.Result), Icons.For(frame.Result), frame));
        while (NgStrip.Count > StripLength) NgStrip.RemoveAt(NgStrip.Count - 1);
    }

    /// <summary>Called about ten times a second: lets held results, notices and the tooltip expire,
    /// and asks for the counters when they are due.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        foreach (var tile in Tiles)
            if (tile.TooltipText is not null && now >= tile.TooltipUntil) tile.HideTooltip();
        if (_link == LinkState.Connected && !_countersAsked && now >= _countersDue) _ = LoadCountersAsync();
        // Without parts the hour still moves on: a new minute redraws the charts too.
        if (now >= _overviewDue && (Parts.Version != _overviewVersion || now.Minute != _overviewMinute)) UpdateOverview(now);
        Refresh();
    }

    /// <summary>Works out the Overview charts from the parts list.</summary>
    public void UpdateOverview(DateTimeOffset now)
    {
        _overviewVersion = Parts.Version;
        _overviewDue = now + OverviewEvery;
        _overviewMinute = now.Minute;
        Overview = OverviewData.From(Parts, now, _strings["run.chart.same_code"], _strings["run.chart.no_reason"], _strings["run.chart.other"],
            r => _strings.Has(r) ? _strings[r] : r);
        CycleNote = _strings.Format("run.chart.cycle.note", Overview.CycleParts.ToString("N0", CultureInfo.CurrentCulture));
    }

    private async Task LoadCountersAsync()
    {
        _countersAsked = true;
        try
        {
            var (reached, counters) = await _calls.TryAsync((c, ct) => c.GetCountersAsync(ct));
            if (reached && counters is not null && _link == LinkState.Connected) _runCounters = counters;
        }
        finally
        {
            _countersAsked = false;
            _countersDue = _time.GetUtcNow() + CountersEvery;
        }
        Refresh();
    }

    /// <summary>A tap on the first camera's live image (image pixels): shows the text of the result graphic there.</summary>
    public void TapImage(Point2 at, double slop) => TapImage(Tiles[0], at, slop);

    /// <summary>A tap on a camera's live image (image pixels): shows the text of the result graphic there.</summary>
    public void TapImage(CameraTile tile, Point2 at, double slop) => ShowTooltipAt(tile, at, slop, keepUntil: false);

    private void ShowTooltipAt(CameraTile tile, Point2 at, double slop, bool keepUntil)
    {
        var hit = GraphicHit.Find(tile.Graphics, at, slop);
        if (hit is null || GraphicHit.Text(hit, tile.Frame?.Codes, _strings) is not { Length: > 0 } text)
        {
            tile.HideTooltip();
            return;
        }
        (tile.TapAt, tile.TapSlop) = (at, slop);
        if (!keepUntil) tile.TooltipUntil = _time.GetUtcNow() + TooltipFor;
        tile.TooltipText = text;
        tile.TooltipAt = at;
    }

    public void RefreshTexts()
    {
        _setupStale = true;   // the setup check's lines in the new language
        UpdateOverview(_time.GetUtcNow());
        Refresh();
    }

    private void Refresh()
    {
        var now = _time.GetUtcNow();
        var connected = _link == LinkState.Connected && _statusFresh && _status is not null;
        // Connected, first status not yet in: still connecting, not "not connected".
        var connecting = _link == LinkState.Connecting || (_link == LinkState.Connected && !connected);
        var status = _status;
        var running = connected && status!.State == LineState.Running;
        IsRunning = running;

        // Result block.
        var verdict = _hold.Current(now);
        if (connecting) SetResult(Tone.Stopped, Icons.Wait, "status.connecting");
        else if (!connected) SetResult(Tone.Ng, Icons.Cross, "result.not_connected");
        else if (status!.Alarm is not null) SetResult(Tone.Ng, Icons.Bang, "result.alarm");
        else if (!running) SetResult(Tone.Stopped, Icons.Pause, "result.stopped");
        else if (verdict == Verdict.None) SetResult(Tone.Info, Icons.Info, "result.waiting");
        else SetResult(Tones.For(verdict), Icons.For(verdict), verdict switch
        {
            Verdict.Ok => "result.ok",
            Verdict.Ng => "result.ng",
            Verdict.Duplicate => "result.duplicate",
            Verdict.Warn => "result.warn",
            _ => "result.info",
        });
        // Camera tiles: each its own verdict frame; one camera's frame is the result block's verdict.
        var newest = Tiles.Max(t => t.FrameAt);
        foreach (var tile in Tiles)
        {
            // the other cameras' pictures come, this one's do not: its picture is not current
            var late = Tiles.Count > 1 && newest - tile.FrameAt > TileStaleAfter;
            tile.Dimmed = !running || tile.Stale || late;
            var shown = Tiles.Count == 1 ? verdict : tile.Hold.Current(now);
            var bad = shown is Verdict.Ng or Verdict.Duplicate;
            // a dimmed picture is never green: only a bad part keeps its colour on it; and while the part
            // is NG (a camera without a picture, the PLC link, a dropped frame) no camera's tile is green
            tile.FrameTone = !running || shown == Verdict.None || (!bad && (tile.Dimmed || verdict is Verdict.Ng or Verdict.Duplicate))
                ? Tone.Neutral : Tones.For(shown);
            tile.ResultIcon = tile.FrameTone == Tone.Neutral ? Icons.Camera : Icons.For(shown);
            tile.ShowHeader = Tiles.Count > 1;
        }
        ImageDimmed = Tiles.All(t => t.Dimmed);
        UpdateLastPart();
        LineSpeedPpm = connected ? status!.LineSpeedPpm : 0;
        CycleBudgetMs = connected ? status!.CycleBudgetMs : 0;
        UpdateBatchPlc(connected ? status : null, running, now);
        UpdateSetupCheck(connected ? status!.SetupCheck : null, running);
        UpdateLearning(connected ? status!.Learning : null);

        // Counters.
        if (status is not null)
        {
            var c = status.Counters;
            GoodText = c.Good.ToString("N0", CultureInfo.CurrentCulture);
            BadText = c.Bad.ToString("N0", CultureInfo.CurrentCulture);
            TotalText = c.Total.ToString("N0", CultureInfo.CurrentCulture);
            GoodPercentText = c.Total == 0 ? "–"
                : (100.0 * c.Good / c.Total).ToString("0.0", CultureInfo.CurrentCulture) + " %";
        }

        // Yield ring and cycle time from GetCounters; until it answers, the yield from the status.
        var counters = connected ? _runCounters : null;
        if (counters is { Total: > 0 })
        {
            YieldValue = Math.Clamp(counters.YieldPercent, 0, 100);
            GoodPercentText = YieldValue.ToString("0.0", CultureInfo.CurrentCulture) + " %";
        }
        else YieldValue = status is { Counters.Total: > 0 } ? 100.0 * status.Counters.Good / status.Counters.Total : -1;
        CycleText = counters is { MedianCycleMs: > 0 }
            ? _strings.Format("run.cycle.ms", counters.MedianCycleMs.ToString("0", CultureInfo.CurrentCulture)) : "–";
        CycleMaxText = counters is { MaxCycleMs: > 0 }
            ? _strings.Format("run.cycle.max", counters.MaxCycleMs.ToString("0", CultureInfo.CurrentCulture)) : "";

        // Banner: the most important problem, with what to do.
        (Tone, string, string, string?)? banner =
            connecting ? null
            : !connected ? (Tone.Ng, Icons.Cross, _strings["banner.not_connected"], null)
            : !status!.LicenceOk ? (Tone.Stopped, Icons.Lock, _strings["banner.locked"], null)
            : !status.PlcOk ? (Tone.Ng, Icons.Plug, _strings["banner.plc_lost"], null)
            : !status.CameraOk ? (Tone.Ng, Icons.Camera, _strings["banner.no_camera"], null)
            : !running && status.Changeover is { AllowsStart: false } co ? ChangeoverBanner(co)
            : status.Alarm is { } alarm ? (Tone.Ng, Icons.Bang, alarm.Length > 0 ? alarm : _strings["banner.alarm"], status.AlarmHelp)
            : status.Warning is { Length: > 0 } warning ? (Tone.Warn, Icons.Bang, warning, status.WarningHelp)
            : null;
        HasBanner = banner is not null;
        if (banner is { } b) (BannerTone, BannerIcon, BannerText, _bannerHelp) = b;
        else _bannerHelp = null;
        HasBannerHelp = _bannerHelp is not null;

        // Actions: always shown, faded with a reason when not possible now.
        StartReasonKey = !connected ? "run.reason.not_connected"
            : running ? "run.reason.already_running"
            : !status!.LicenceOk ? "run.reason.locked"
            : !status.CameraOk ? "run.reason.no_camera"
            : !status.PlcOk ? "run.reason.plc_lost"
            : status.Changeover is { AllowsStart: false } ? "run.reason.changeover"
            : null;
        CanAskAgain = connected && !running && status!.Changeover?.State == ChangeoverCheck.Refused;
        CanAcceptHere = connected && !running && status!.Changeover?.State == ChangeoverCheck.NoServer;
        StopReasonKey = !connected ? "run.reason.not_connected" : !running ? "run.reason.already_stopped" : null;
        // A running line may refuse a change ("Press Stop first"); the runtime decides.
        ChangeReasonKey = !connected ? "run.reason.not_connected" : null;
        BatchReasonKey = !connected ? "run.reason.not_connected" : running ? "run.reason.stop_first" : null;
        BatchAvailable = BatchReasonKey is null;
        var plcNow = connected ? status!.Plc : null;
        PlcTestShown = MayTestPlc?.Invoke() == true && plcNow is not null;
        PlcTestReasonKey = !connected ? "run.reason.not_connected"
            : running ? "run.reason.stop_first"
            : plcNow is not { LinkUp: true, HeartbeatOk: true } ? "run.plc.test.no_link"
            : plcNow.RunEnable ? "run.plc.test.run_enable"
            : null;
        PlcTestAvailable = PlcTestShown && PlcTestReasonKey is null;
        StartAvailable = StartReasonKey is null;
        StopAvailable = StopReasonKey is null;
        ChangeAvailable = ChangeReasonKey is null;

        // Short notice above the action bar.
        HasNotice = _noticeKey is not null && now < _noticeUntil;
        NoticeText = !HasNotice ? "" : _refusalText ?? _strings.Format(_noticeKey!, _noticeArgs);
    }

    // The supervisor's check of the changeover (CHANGEOVER-VERIFY.md): the code is the one their screen shows.
    private (Tone, string, string, string?) ChangeoverBanner(ChangeoverInfo co) => co.State switch
    {
        ChangeoverCheck.Refused => (Tone.Ng, Icons.Bang, co.Note.Length > 0
            ? _strings.Format("banner.changeover.refused_note", co.By, co.Note) : _strings.Format("banner.changeover.refused", co.By), null),
        ChangeoverCheck.NoServer => (Tone.Ng, Icons.Lock, _strings["banner.changeover.no_server"], null),
        _ => (Tone.Warn, Icons.User, _strings.Format("banner.changeover.waiting", co.Code), null),
    };

    /// <summary>After a refusal, when the values are right: ask the supervisor again with the same values.</summary>
    [ObservableProperty] private bool _canAskAgain;

    [RelayCommand]
    private async Task AskAgainAsync()
    {
        if (!CanAskAgain) return;
        var (reached, r) = await _calls.TryDesignAsync((c, ct) => c.AskChangeoverAgainAsync(ct));
        if (!reached || r is null) ShowNotice("run.reason.no_answer");
        else if (r.Ok) ShowNotice("run.changeover.asked");
        else ShowRefusal(r);
    }

    /// <summary>The plant server cannot be reached: a second person may accept the changeover here (owner 2026-11-01).</summary>
    [ObservableProperty] private bool _canAcceptHere;

    [RelayCommand]
    private void AcceptHere()
    {
        if (!CanAcceptHere || _status?.Changeover is not { State: ChangeoverCheck.NoServer } co) return;
        _overlays.Show(new AcceptChangeoverViewModel(_strings, _calls, _status.Product?.Name ?? _strings["top.no_product"],
            _status.Batch, co.Code, _overlays.Close, () => ShowNotice("run.changeover.accepted_here")));
    }

    /// <summary>The Batch and PLC card (UI-UX-SPEC.md 4.1). Nothing is shown as good while the
    /// runtime is not reachable; a PLC signal is green only while the link and heartbeat are.</summary>
    /// <summary>The setup check card: hidden while the line runs (the check never runs then) and when nothing was checked.</summary>
    private void UpdateSetupCheck(SetupCheckOutcome? check, bool running)
    {
        if (!_setupStale && ReferenceEquals(check, _setupShown) && running == _setupRunning) return;
        (_setupShown, _setupRunning, _setupStale) = (check, running, false);
        var lines = running ? [] : SetupAdvice.Lines(_strings, check);
        if (!lines.SequenceEqual(SetupLines))
        {
            SetupLines.Clear();
            foreach (var line in lines) SetupLines.Add(line);
        }
        HasSetupCheck = SetupLines.Count > 0;
    }

    private void UpdateBatchPlc(RuntimeStatus? status, bool running, DateTimeOffset now)
    {
        var batch = (status?.Batch ?? []).Select(b => b.Value.Length == 0
            ? new BatchLine(b.Label, _strings["run.batch.missing"], true) : new BatchLine(b.Label, b.Value, false)).ToList();
        if (!batch.SequenceEqual(BatchLines))
        {
            BatchLines.Clear();
            foreach (var line in batch) BatchLines.Add(line);
        }
        BatchNote = status is null ? "" : batch.Count == 0 ? _strings["run.batch.none"] : "";

        var plc = status?.Plc;
        var live = plc is { LinkUp: true, HeartbeatOk: true };
        // a new last part: timed from when this screen saw it, so another PC's clock never keeps it green
        if (plc?.LastResultTime != _plcLastTime) (_plcLastTime, _plcLastSeen) = (plc?.LastResultTime, now);
        SignalLine Lamp(string key, bool? on, Tone off) => on switch
        {
            true => new SignalLine(Tone.Ok, Icons.Check, _strings[key], ""),
            false => new SignalLine(off, off == Tone.Ng ? Icons.Cross : Icons.Pause, _strings[key], ""),
            null => new SignalLine(Tone.Neutral, Icons.Info, _strings[key], ""),
        };
        List<SignalLine> lamps = plc is null ? [] :
        [
            // a link without the heartbeat is no working PLC: not green
            Lamp("run.plc.link", plc.LinkUp ? (plc.HeartbeatOk ? true : null) : false, Tone.Ng),
            Lamp("run.plc.heartbeat", plc.LinkUp ? plc.HeartbeatOk : null, Tone.Ng),
            // RunEnable read over a lost link is not known
            Lamp("run.plc.run_enable", live ? plc.RunEnable : null, Tone.Stopped),
        ];
        if (!lamps.SequenceEqual(PlcLines))
        {
            PlcLines.Clear();
            foreach (var lamp in lamps) PlcLines.Add(lamp);
        }
        PlcNote = status is null ? _strings["run.plc.unknown"] : plc is null ? _strings["run.plc.none"] : "";

        HasPlcLast = plc is not null;
        if (plc is null) return;
        var parts = new List<string>();
        if (plc.LastResultTime is { } at)
            parts.Add(_strings.Format("run.plc.last", _strings[plc.LastResultPass ? "result.ok" : "result.ng"],
                at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)));
        if (plc.LastTriggerId > 0) parts.Add(_strings.Format("run.plc.trigger", plc.LastTriggerId.ToString(CultureInfo.CurrentCulture)));
        PlcLastText = parts.Count == 0 ? _strings["run.plc.no_part"] : string.Join(" · ", parts);
        // the PLC's last result in colour only while the line runs over a live link and the part is recent
        var current = live && running && plc.LastResultTime is not null && now - _plcLastSeen < ResultHold.LastFor;
        PlcLastTone = !current ? Tone.Neutral : plc.LastResultPass ? Tone.Ok : Tone.Ng;
        PlcLastIcon = PlcLastTone switch { Tone.Ok => Icons.Check, Tone.Ng => Icons.Cross, _ => Icons.Info };
    }

    private void SetResult(Tone tone, string icon, string key)
    {
        ResultTone = tone;
        ResultIcon = icon;
        ResultText = _strings[key];
    }

    public void ShowNotice(string key, params object[] args)
    {
        _noticeKey = key;
        _refusalText = null;
        _noticeArgs = args;
        _noticeUntil = _time.GetUtcNow() + NoticeFor;
        Refresh();
    }

    /// <summary>A refusal from the runtime: our words for its key, else its own text.</summary>
    public void ShowRefusal(CommandResult result)
    {
        ShowNotice("run.reason.refused");
        _refusalText = _strings.KeyOrText(result.ReasonKey, result.ReasonText, "run.reason.refused");
        Refresh();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (StartReasonKey is { } reason)
        {
            ShowNotice(reason);
            return;
        }
        // the batch not entered yet: Start batch first, then the line starts
        if (_status?.Batch is { } batch && batch.Any(b => b.Value.Length == 0))
        {
            _overlays.Show(new BatchViewModel(_strings, _calls, batch, _overlays.Close, () => RunCommandAsync((c, ct) => c.StartAsync(ct))));
            return;
        }
        await RunCommandAsync((c, ct) => c.StartAsync(ct));
    }

    /// <summary>Start batch: the values the job checks against, while the line is stopped.</summary>
    [RelayCommand]
    private void Batch()
    {
        if (BatchReasonKey is { } reason)
        {
            ShowNotice(reason);
            return;
        }
        _overlays.Show(new BatchViewModel(_strings, _calls, _status?.Batch ?? [], _overlays.Close));
    }

    public string? BatchReasonKey { get; private set; }
    [ObservableProperty] private bool _batchAvailable;

    /// <summary>The OK and NG test signal buttons on the Batch and PLC card (PLC-INTERFACE.md section 7,
    /// UI-UX-SPEC.md "Test signal"): shown to an Engineer or Admin while a PLC is set up, usable only in
    /// Teach (the line stopped) with the PLC link live and RunEnable off. The runtime decides again.</summary>
    [ObservableProperty] private bool _plcTestShown;
    [ObservableProperty] private bool _plcTestAvailable;
    public string? PlcTestReasonKey { get; private set; }

    [RelayCommand]
    private void PlcTestOk() => PlcTest(true);

    [RelayCommand]
    private void PlcTestNg() => PlcTest(false);

    /// <summary>The test signal asks once whether the machine is safe, then pulses OK or NG with Done for
    /// 2 s. A test signal is no part: it is not counted, logged or shown as a part's result.</summary>
    private void PlcTest(bool ok)
    {
        if (!PlcTestShown) return;
        if (PlcTestReasonKey is { } reason)
        {
            ShowNotice(reason);
            return;
        }
        _overlays.Show(new ConfirmViewModel(_strings, "run.plc.test.question", "run.plc.test.yes", "common.no", async () =>
        {
            _overlays.Close();
            var (reached, result) = await _calls.TryAsync((c, ct) => c.PlcTestSignalAsync(ok, ct));
            if (!reached) ShowNotice("run.reason.not_connected");
            else if (result is null) ShowNotice("run.reason.refused");
            else if (result.Ok) ShowNotice(ok ? "run.plc.test.sent_ok" : "run.plc.test.sent_ng");
            else
            {
                if (result.ReasonKey == "run.reason.sign_in") SignInNeeded?.Invoke();
                ShowRefusal(result);
            }
        }, _overlays.Close));
    }

    [RelayCommand]
    private void Stop()
    {
        if (StopReasonKey is { } reason)
        {
            ShowNotice(reason);
            return;
        }
        // Stopping a running line asks once (UI-UX-SPEC.md principle 9).
        _overlays.Show(new ConfirmViewModel(_strings, "run.stop.question", "run.stop.yes", "common.no", async () =>
        {
            _overlays.Close();
            await RunCommandAsync((c, ct) => c.StopAsync(ct));
        }, _overlays.Close));
    }

    [RelayCommand]
    private void ChangeProduct()
    {
        if (ChangeReasonKey is { } reason)
        {
            ShowNotice(reason);
            return;
        }
        var picker = new ProductPickerViewModel(_strings, _calls, _status?.Product, _overlays, ShowNotice, _decode, _pictures);
        _overlays.Show(picker);
        _ = picker.LoadAsync();
    }

    // --- the learning session (owner, 2026-11-04) -------------------------------------------------

    /// <summary>The small corner notice "Line idle: n pictures waiting for supervisor sorting": shown while the session
    /// is on, the line idle and pictures wait; closed with ✕ until the line has worked again. It never starts learning.</summary>
    [ObservableProperty] private bool _hasLearningNotice;
    [ObservableProperty] private string _learningNoticeText = "";
    /// <summary>The Learning button: Supervisor, QA, Engineer and Admin.</summary>
    [ObservableProperty] private bool _canSort;
    private bool _learningNoticeClosed;
    /// <summary>"12 bad parts wait for a review (3 marked Was OK)" in the Bad parts tab, for a supervisor (AS-04i).</summary>
    [ObservableProperty] private string _reviewWaitingText = "";
    [ObservableProperty] private bool _hasReviewWaiting;
    private LearningInfo? _learning;

    private void UpdateLearning(LearningInfo? l)
    {
        CanSort = MayReview?.Invoke() == true;
        // the line worked again: a notice closed earlier may come back at the next idle time
        if (l is null || !l.Idle) _learningNoticeClosed = false;
        HasLearningNotice = l is { Enabled: true, Idle: true, ToSort: > 0 } && !_learningNoticeClosed;
        LearningNoticeText = l is null ? "" : _strings.Format("run.learning.notice", l.ToSort);
        // whether the line runs or not, whatever the setting (AS-04i)
        _learning = l;
        HasReviewWaiting = CanSort && l is { BadToReview: > 0 };
        ReviewWaitingText = l is not { BadToReview: > 0 } ? ""
            : l.FlaggedOk > 0 ? _strings.Format("run.review.waiting_flagged", l.BadToReview, l.FlaggedOk)
            : _strings.Format("run.review.waiting", l.BadToReview);
    }

    /// <summary>The bad parts waiting for a review: the sorting on them (those marked "Was OK" first) while the learning
    /// session is on, else the NG viewer on the newest bad part.</summary>
    [RelayCommand]
    private void OpenReviewWaiting()
    {
        if (MayReview?.Invoke() != true) { ShowNotice("run.learning.supervisor"); return; }
        if (_learning is { Enabled: true } l)
        {
            var sort = new SortViewModel(_strings, _calls, _decode, _overlays) { Filter = l.FlaggedOk > 0 ? SortFilter.WasOk : SortFilter.Bad };
            _overlays.Show(sort);
            _ = sort.LoadAsync();
        }
        else if (NgStrip.Count > 0) OpenNg(NgStrip[0]);
    }

    [RelayCommand]
    private void CloseLearningNotice()
    {
        _learningNoticeClosed = true;
        HasLearningNotice = false;
    }

    /// <summary>The sorting of the saved good and bad pictures, and learning from them (Supervisor and above).</summary>
    [RelayCommand]
    private void OpenSorting()
    {
        if (MayReview?.Invoke() != true) { ShowNotice("run.learning.supervisor"); return; }
        var sort = new SortViewModel(_strings, _calls, _decode, _overlays);
        _overlays.Show(sort);
        _ = sort.LoadAsync();
    }

    /// <summary>A tap on a bad part in the strip: the NG image viewer on it (UI-UX-SPEC.md 4.3).</summary>
    [RelayCommand]
    private void OpenNg(NgThumb thumb)
    {
        var parts = NgStrip.ToList();
        var index = parts.IndexOf(thumb);
        if (index < 0) return;
        var viewer = new NgViewerViewModel(_strings, _calls, parts, index, _decode, _overlays) { MayReview = MayReview?.Invoke() == true };
        _overlays.Show(viewer);
        _ = viewer.LoadAsync();
        _ = viewer.LoadReviewsAsync();
    }

    /// <summary>"What do I do?" on the banner: the alarm's steps (UI-UX-SPEC.md 4.4).</summary>
    [RelayCommand]
    private void WhatToDo()
    {
        if (_bannerHelp is not { } help) return;
        _overlays.Show(new AlarmHelpViewModel(_strings, BannerText, BannerTone, help, _overlays.Close));
    }

    /// <summary>"Parts": every part's own result, newest first (requirements 6.1).</summary>
    [RelayCommand]
    private void OpenParts() => _overlays.Show(new PartsViewModel(_strings, Parts, _time, _overlays.Close));

    /// <summary>"Bad parts" on the toolbar: the strip of the last bad parts under the image.</summary>
    [RelayCommand]
    private void ShowBadParts() => BottomTab = 1;

    [RelayCommand]
    private void Help() => _overlays.Show(new MessageViewModel(_strings, "help.run.title", "help.run.body", _overlays.Close));

    private async Task RunCommandAsync(Func<IRuntimeClient, CancellationToken, Task<CommandResult>> call)
    {
        var (reached, result) = await _calls.TryAsync(call);
        if (!reached) ShowNotice("run.reason.not_connected");
        else if (result is null) ShowNotice("run.reason.refused");
        else if (result is { Ok: false })
        {
            if (result.ReasonKey == "run.reason.sign_in") SignInNeeded?.Invoke();
            ShowRefusal(result);
        }
    }
}
