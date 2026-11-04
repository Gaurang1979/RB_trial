using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BarcodeReading.Protection;
using Microsoft.Extensions.Options;
using StationGuard;

namespace BarcodeReading.Dashboard.Services;

public enum SourceKind
{
    /// <summary>Hikrobot cameras (top: child codes, side: parent code).</summary>
    Camera,
    /// <summary>Replays a folder of saved BMP images: testing and demos without a camera.</summary>
    Replay,
}

/// <summary>The latest case, as shown on the Live page.</summary>
public sealed record CaseView(long Id, DateTime TimeUtc, DecodeResult Result, string? ParentCode, bool Ok, string? Source,
    DuplicateCheck? Duplicates = null, int ImageWidth = 0, int ImageHeight = 0);

/// <summary>An auto-calibration: the proposal, and the low-resolution picture of what was seen.</summary>
public sealed record CalibrationView(string Product, CalibrationResult Result, CameraSettings Current, byte[] PreviewJpeg, string Source)
{
    /// <summary>Print grades of the codes read on the calibration picture (verifier-style when the
    /// grading calibration is set), worst first.</summary>
    public IReadOnlyList<PrintGrade> Print { get; init; } = Array.Empty<PrintGrade>();
    public GradingCalibration Grading { get; init; } = new();
}

/// <summary>One code's print grade: ISO 15415 (2D) or 15416 (1D); 4 = A .. 0 = F.</summary>
public sealed record PrintGrade(string Text, int Grade, float SymbolContrast, float Modulation, float XDimMm, bool Calibrated, bool Linear)
{
    public string Letter => Grade switch { 4 => "A", 3 => "B", 2 => "C", 1 => "D", 0 => "F", _ => "-" };
}

/// <summary>What the station is doing right now.</summary>
public sealed record StationSnapshot(
    bool Running, string? Product, SourceKind Source, string? StartedBy, DateTime? StartedUtc, string? Error,
    long Cases, long OkCases, CaseView? Last, double ReadRate, IReadOnlyList<Advice> Advice, int PreviewVersion,
    bool ModelLoaded, bool EnhancerLoaded, bool PreviewSearching = false, ZoneStats? Zones = null,
    IReadOnlyList<(MissReason Reason, int Count)>? MissReasons = null, int MissReasonCases = 0, StationHealth? Health = null);

/// <summary>
/// Runs one product on the line: frame in, decode with the tuning agent's settings, pair with the
/// parent code, log the case, feed the advisor, render the live picture, save short cases for
/// the retraining agent (off this thread, through the NgImageStore, as the ResourceGuard allows).
/// One frame at a time; the engine itself uses all CPU cores.
/// </summary>
public sealed class Station : IDisposable
{
    private readonly StationOptions _opt;
    private readonly ProductStore _products;
    private readonly CaseLog _log;
    private readonly DuplicateGuard _dups;
    private readonly ILogger<Station> _logger;
    private readonly ResourceGuard _guard;
    private readonly NgImageStore _hardCases;
    private readonly HostLicence _licence;
    private readonly object _frameLock = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<string, TuningAgent> _tuners = new();

    private ProductMaster? _product;
    private CodeReader? _reader;
    private LineAdvisor _advisor = new();
    private bool _modelLoaded, _enhancerLoaded;
    private CancellationTokenSource? _cts;
    private Thread? _replayThread;
    private CameraSource? _camera;
    private SourceKind _source;
    private string? _startedBy, _error;
    private DateTime? _startedUtc;
    private long _cases, _okCases;
    private CaseView? _last;
    private byte[]? _previewJpeg, _foundJpeg;
    private int _previewVersion;
    private bool _previewSearching;
    private ZoneStats? _zones;
    private readonly MissReasonTally _missReasons = new(); // why codes did not read, since the start (the shift)
    private long _frameSeq; // the yellow picture of a frame never replaces a later picture
    private (string Text, DateTime Utc)? _lastParent;

    /// <summary>Raised after every case and on start / stop. Handlers must be quick.</summary>
    public event Action? Changed;

    // Pages listen here, on the station's thread: one that throws (a page closing) must never
    // stop the inspection or the other listeners.
    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (var h in handlers.GetInvocationList())
            try { ((Action)h)(); } catch (Exception) { }
    }

    public Station(IOptions<StationOptions> opt, ProductStore products, CaseLog log, DuplicateGuard dups, ILogger<Station> logger,
        ResourceGuard guard, NgImageStore hardCases, HostLicence? licence = null)
    {
        _licence = licence ?? new HostLicence(null);
        _dups = dups;
        _opt = opt.Value;
        _products = products;
        _log = log;
        _logger = logger;
        _guard = guard;
        _hardCases = hardCases;
        if (!hardCases.ImagesEnabled) _logger.LogWarning("station: the hard-case folder cannot be used; cases are counted, no images are saved");
        // the station is told: the Live page shows the warning, the audit trail keeps when it started and ended
        guard.LevelChanged += (from, to, why) =>
        {
            if ((from >= Pressure.High) == (to >= Pressure.High)) return;
            _logger.LogWarning("station: PC pressure {From} -> {To} ({Why})", from, to, why);
            try { _log.Audit("system", "pc-pressure", $"{from} -> {to}: {why}"); }
            catch (Exception e) { _logger.LogWarning(e, "could not write the pressure change to the audit trail"); }
            RaiseChanged();
        };
    }

    /// <summary>The resource guard: its status and its limits (<see cref="ResourceGuard.Options"/>).</summary>
    public ResourceGuard Guard => _guard;

    /// <summary>Hard-case images and the counts of every case, by reason.</summary>
    public NgImageStore HardCaseImages => _hardCases;

    public StationSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            return new StationSnapshot(_cts is not null, _product?.ProductCode, _source, _startedBy, _startedUtc, _error,
                _cases, _okCases, _last, _advisor.ReadRate,
                _product is null ? Array.Empty<Advice>() : _advisor.Advise(_product.ChildCamera, _product.TimeBudgetMs),
                _previewVersion, _modelLoaded, _enhancerLoaded, _previewSearching, _zones,
                _missReasons.Counts(), _missReasons.Cases, HardCases.Health(_guard.Status, _hardCases.Counts, _hardCases.ImagesEnabled));
        }
    }

    /// <summary>The last decoded case: read codes green, unread regions red.</summary>
    public byte[]? PreviewJpeg
    {
        get { lock (_stateLock) return _previewJpeg; }
    }

    /// <summary>The case being decoded: found regions yellow (the last one, if none is decoding).</summary>
    public byte[]? FoundJpeg
    {
        get { lock (_stateLock) return _foundJpeg; }
    }

    /// <summary>The tuning agent of a product (created and loaded from disk on first use).</summary>
    public TuningAgent Tuner(string productCode)
    {
        lock (_tuners)
        {
            if (!_tuners.TryGetValue(productCode, out var t))
            {
                t = new TuningAgent();
                t.Load(TuningPath(productCode));
                _tuners[productCode] = t;
            }
            return t;
        }
    }

    private string TuningPath(string productCode) => Path.Combine(_opt.ProductsFolder, productCode + ".tuning.json");
    private string LearnedPath(string productCode) => Path.Combine(_opt.ProductsFolder, productCode + ".learned");
    private const int LearnedSaveEvery = 50; // cases between saves of the reader's learned state

    /// <summary>
    /// The key a saved learned state must carry to be loaded: the product, its version, its child
    /// camera settings and the engine. Learned state never crosses products, and a change to any of
    /// these starts the reader afresh (the state would describe other pictures or other steps).
    /// </summary>
    internal static string LearnedKey(ProductMaster product)
    {
        byte[] cam = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(product.ChildCamera));
        return $"{product.ProductCode} v{product.Version} camera {Convert.ToHexString(cam, 0, 8)} engine {CodeReader.EngineVersion}";
    }

    private void LoadLearned(ProductMaster product, CodeReader reader)
    {
        string path = LearnedPath(product.ProductCode);
        try
        {
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            string? key = text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("key ", StringComparison.Ordinal));
            if (key != "key " + LearnedKey(product))
            {
                _logger.LogInformation("learned state of {Product} is for another product version, camera or engine; starting afresh", product.ProductCode);
                return;
            }
            reader.SetLearned(text); // the key line is one the engine ignores
            _logger.LogInformation("learned state of {Product} loaded", product.ProductCode);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "learned state of {Product} not loaded; starting afresh", product.ProductCode);
        }
    }

    private void SaveLearned(ProductMaster product, CodeReader reader)
    {
        try
        {
            string text = reader.GetLearned() + "key " + LearnedKey(product) + "\n";
            string path = LearnedPath(product.ProductCode);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; // two savers never share a temp file
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "learned state of {Product} not saved", product.ProductCode);
        }
    }

    // Start and Stop one at a time, and Start only after Stop has fully finished (camera disposed,
    // grab and replay threads joined): otherwise Start could free the reader while the last frame
    // is still decoding, or open the camera while it is still open. Frame threads never take it.
    private readonly object _lifeLock = new();

    /// <summary>
    /// Line safety (requirements 18.3 ENC-HOST-01, PROT-04/05): refuses with the NG reason, told on the Live page and
    /// in the audit trail, when this PC needs a licence and the content key is not available. Nothing is loaded, so
    /// there is no fallback to a plaintext model or product.
    /// </summary>
    private void RefuseWithoutKey(string user, string what, string productCode)
    {
        if (_licence.InspectionBlockedReason() is not { } why) return;
        RefuseNg(user, what, productCode, why);
    }

    private void RefuseNg(string user, string what, string productCode, string why)
    {
        try { _log.Audit(user, what + "-refused", $"{productCode}: NG, {why}"); }
        catch (Exception e) { _logger.LogWarning(e, "could not write the refused {What} to the audit trail", what); }
        SetError("NG: " + why);
        throw new InvalidOperationException($"{what} refused (NG): {why}");
    }

    public void Start(string productCode, SourceKind source, string user, string? replayFolder = null)
    {
        RefuseWithoutKey(user, "start", productCode);
        try { StartLoaded(productCode, source, user, replayFolder); }
        catch (ContentProtectionException e) { RefuseNg(user, "start", productCode, e.Message); } // encrypted, no key
    }

    private void StartLoaded(string productCode, SourceKind source, string user, string? replayFolder)
    {
        lock (_lifeLock)
        lock (_stateLock)
        {
            if (_cts is not null) throw new InvalidOperationException("the station is already running; stop it first");
            var product = _products.Get(productCode) ?? throw new InvalidOperationException($"product {productCode} not found");

            _reader?.Dispose();
            _reader = new CodeReader(product.ChildReaderSettings());
            string model = Path.Combine(_opt.ModelsFolder, "codenet.onnx");
            string enhancer = Path.Combine(_opt.ModelsFolder, "enhancer.onnx");
            _modelLoaded = File.Exists(model);
            _enhancerLoaded = File.Exists(enhancer);
            if (_modelLoaded) _reader.LoadModel(model);
            if (_enhancerLoaded) _reader.LoadEnhancer(enhancer);
            // a shift starts where the last one stopped (camera sources only: a replay of the same
            // pictures would teach the reader its own test images)
            if (source != SourceKind.Replay) LoadLearned(product, _reader);

            _product = product;
            _advisor = new LineAdvisor();
            _source = source;
            _startedBy = user;
            _startedUtc = DateTime.UtcNow;
            _error = null;
            _cases = _okCases = 0;
            _missReasons.Reset();
            _last = null;
            _lastParent = null;
            _cts = new CancellationTokenSource();

            if (source == SourceKind.Replay)
            {
                string folder = replayFolder ?? _opt.ReplayFolder ?? throw new InvalidOperationException("no replay folder configured");
                if (!Directory.Exists(folder)) throw new InvalidOperationException($"replay folder not found: {folder}");
                var token = _cts.Token;
                _replayThread = new Thread(() => ReplayLoop(folder, token)) { IsBackground = true, Name = "station replay" };
                _replayThread.Start();
            }
            else
            {
                _camera = CameraSource.Open(product, _opt, OnRawFrame, OnParentCode, e => SetError(e));
            }
            _hardCases.StartBatch($"{product.ProductCode} {_startedUtc:yyyy-MM-dd HH:mm:ss}Z");
            _log.Audit(user, "start", $"{product.ProductCode} v{product.Version}, {source}");
        }
        RaiseChanged();
    }

    public void Stop(string user)
    {
        lock (_lifeLock) StopLocked(user);
    }

    private void StopLocked(string user)
    {
        Thread? t;
        ProductMaster? learnedProduct = null;
        CodeReader? learnedReader = null;
        CameraSource? camera;
        string? tuneCode = null;
        lock (_stateLock)
        {
            if (_cts is null) return;
            _cts.Cancel();
            t = _replayThread;
            _replayThread = null;
            // Disposed below, outside the lock: disposing joins the camera's grab thread, and a
            // frame in flight on that thread waits for this lock (OnRawFrame / ProcessFrame), so
            // disposing here could deadlock Stop.
            camera = _camera;
            _camera = null;
            _cts = null;
            tuneCode = _product?.ProductCode;
            if (_product is not null && _reader is not null && _source != SourceKind.Replay && _cases > 0)
            {
                learnedProduct = _product;
                learnedReader = _reader;
            }
            _log.Audit(user, "stop", $"{_product?.ProductCode}, {_cases} cases, {_okCases} OK");
        }
        camera?.Dispose();
        t?.Join(TimeSpan.FromSeconds(10));
        if (tuneCode is not null) Tuner(tuneCode).Save(TuningPath(tuneCode));
        // a burst cut short by the stop must not hold its frame copies until the next start
        lock (_frameLock) _burst.Clear();
        // after the last frame (the frame lock), outside the state lock: no decode is running, and no
        // file IO under the lock the pages read
        if (learnedProduct is not null && learnedReader is not null)
            lock (_frameLock) SaveLearned(learnedProduct, learnedReader);
        RaiseChanged();
    }

    private void SetError(string e)
    {
        lock (_stateLock) _error = e;
        _logger.LogWarning("station: {Error}", e);
        RaiseChanged();
    }

    private void OnParentCode(string text)
    {
        lock (_stateLock) _lastParent = (text, DateTime.UtcNow);
    }

    // frames of one case when the product takes several per case (FramesPerCase > 1): the PLC
    // triggers that many times in a burst; consecutive camera frames are grouped by count
    private readonly List<byte[]> _burst = new();
    private (int W, int H, int Stride) _burstShape;

    private void OnRawFrame(IntPtr pixels, int w, int h, int stride)
    {
        int perCase;
        lock (_stateLock) perCase = Math.Clamp(_product?.ChildCamera.FramesPerCase ?? 1, 1, 3);
        if (perCase <= 1)
        {
            ProcessFrame(pixels, w, h, stride, "camera");
            return;
        }
        lock (_frameLock)
        {
            if (_burst.Count > 0 && (_burstShape.W != w || _burstShape.H != h || _burstShape.Stride != stride)) _burst.Clear();
            _burstShape = (w, h, stride);
            var copy = new byte[(long)stride * h];
            Marshal.Copy(pixels, copy, 0, copy.Length);
            _burst.Add(copy);
            if (_burst.Count < perCase) return;
            var frames = _burst.ToArray();
            _burst.Clear();
            var handle = GCHandle.Alloc(frames[^1], GCHandleType.Pinned);
            try { ProcessFrame(handle.AddrOfPinnedObject(), w, h, stride, "camera", frames); }
            finally { handle.Free(); }
        }
    }

    private void ReplayLoop(string folder, CancellationToken token)
    {
        var files = Directory.GetFiles(folder, "*.bmp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            SetError($"no .bmp images in {folder}");
            return;
        }
        for (int i = 0; !token.IsCancellationRequested; i = (i + 1) % files.Length)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                var (px, w, h) = LoadGrayImage(files[i]);
                var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
                try { ProcessFrame(handle.AddrOfPinnedObject(), w, h, w, Path.GetFileName(files[i])); }
                finally { handle.Free(); }
            }
            catch (Exception e)
            {
                SetError($"{Path.GetFileName(files[i])}: {e.Message}");
            }
            int wait = _opt.ReplayIntervalMs - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            if (wait > 0) token.WaitHandle.WaitOne(wait);
        }
    }

    // frames: the case's frames when there are several (pixels = the last of them, for the preview)
    private void ProcessFrame(IntPtr pixels, int w, int h, int stride, string source, byte[][]? frames = null)
    {
        lock (_frameLock)
        {
            ProductMaster product;
            CodeReader reader;
            SourceKind sourceKind;
            bool needParent;
            lock (_stateLock)
            {
                if (_product is null || _reader is null || _cts is null) return;
                sourceKind = _source;
                product = _product;
                reader = _reader;
                // read with the product, not later: Stop clears _camera while this frame may still
                // be decoding, and a missing parent code must never pass as OK
                needParent = _camera?.HasParentCamera == true;
            }
            _guard.PartStarted();
            try
            {
                var tuner = Tuner(product.ProductCode);
                reader.Settings = tuner.Next(product);
                // synchronous on purpose: a task reading the camera's buffer could outlive it when the
                // decode throws (the grab loop frees the buffer as soon as this method returns)
                var (small, sw, sh, f) = Preview.Shrink(pixels, w, h, stride);
                long seq;
                lock (_stateLock) seq = ++_frameSeq;
                // yellow boxes as soon as finding is done; drawn off this thread so decoding never waits
                var result = frames is { Length: > 1 }
                    ? reader.DecodeFrames(frames, w, h, stride)
                    : reader.Decode(pixels, w, h, stride, found => Task.Run(() => ShowFound(seq, small, sw, sh, f, found)));
                tuner.Observe(result.Stats, reader.Settings.TimeBudgetMs);

                string? parent = null;
                lock (_stateLock)
                {
                    if (_lastParent is { } lp && (DateTime.UtcNow - lp.Utc).TotalSeconds < 3)
                    {
                        parent = lp.Text;
                        _lastParent = null;
                    }
                }
                // a child or case code already on a recent accepted case: re-used or copied label
                var texts = result.Codes.Select(c => c.Text).ToList();
                var dup = _dups.Check(texts, parent);
                // a parent (case) code must carry valid GS1 check digits when the product is GS1 (it
                // declares a GTIN): a misread SSCC or GTIN fails them. A plain Code 128 is not judged.
                // an EAN-13 parent must be the product's own GTIN (its one check digit passes a misread
                // one time in ten)
                bool parentOk = parent is null || string.IsNullOrEmpty(product.Gtin) ||
                    (product.ParentFormat == CodeFormat.Ean13 ? Gs1Data.SameGtin(parent, product.Gtin) : (Gs1Data.TryParse(parent)?.CheckDigitValid ?? true));
                // more codes than the case holds: a foreign or extra pack in it
                bool ok = result.Stats.Complete && !result.Stats.Extra && (!needParent || parent is not null) && parentOk && !dup.Any;
                var reasons = HardCases.Reasons(result.Stats.Complete, needParent && parent is null, !parentOk, dup.Any, result.Stats.Extra);
                long id = _log.AddCase(product.ProductCode, result, parent, _startedBy, ok, dup.Children.Count, dup.Parent);
                if (ok) _dups.Accept(texts, parent);

                byte[] jpeg = Preview.Render(small, sw, sh, f, result, dup.Children, product.ChildCamera.Roi);

                // after the verdict, which it never changes: copied only when it will be kept, written off this thread
                KeepHardCase(product, reader.Settings, ok, result.Stats.Complete, reasons, pixels, w, h, stride, frames);

                bool saveLearned = false;
                lock (_stateLock)
                {
                    _advisor.Add(result);
                    _missReasons.Add(result);
                    _cases++;
                    if (ok) _okCases++;
                    saveLearned = sourceKind != SourceKind.Replay && _cases % LearnedSaveEvery == 0;
                    _last = new CaseView(id, DateTime.UtcNow, result, parent, ok, source, dup, w, h);
                    _zones = reader.GetZoneStats();
                    _previewJpeg = jpeg;
                    _previewVersion++;
                    _previewSearching = false;
                    _frameSeq++; // a yellow picture still being drawn for this frame is now stale
                }
                if (saveLearned) SaveLearned(product, reader); // every 50 cases, so a crash loses little
                if (tuner.Frames % 50 == 0) tuner.Save(TuningPath(product.ProductCode));
            }
            finally { _guard.PartFinished(); }
        }
        RaiseChanged();
    }

    private void ShowFound(long seq, byte[] small, int w, int h, int factor, IReadOnlyList<FoundRegion> found)
    {
        try
        {
            byte[] jpeg = Preview.RenderFound(small, w, h, factor, found);
            lock (_stateLock)
            {
                if (seq != _frameSeq) return; // decoding already finished: the green/red picture stays
                _foundJpeg = jpeg;
                _previewVersion++;
                _previewSearching = true;
            }
            RaiseChanged();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "could not draw the found regions");
        }
    }

    /// <summary>
    /// Auto-calibration for a product (station stopped): on the top camera, by software trigger,
    /// the product's last good settings first, then a short probe frame and exposure / gain trials
    /// (codes clipped at the sensor maximum rule a setting out), and two frames 80 ms apart at the
    /// setting found when cases move (the speed); in Replay mode, one saved image. Proposes camera,
    /// crop and light settings. Nothing is saved until <see cref="ApplyCalibration"/>.
    /// </summary>
    public CalibrationView Calibrate(string productCode, SourceKind source, string user, CaseMotion motion, LightSettings light,
                                     string? replayImage = null)
    {
        lock (_stateLock)
            if (_cts is not null) throw new InvalidOperationException("stop the line before calibrating");
        RefuseWithoutKey(user, "calibrate", productCode);
        var product = _products.Get(productCode) ?? throw new InvalidOperationException($"product {productCode} not found");
        var current = product.ChildCamera.Clone();
        product.ChildCamera.Motion = motion;
        product.ChildCamera.Light = light;

        using var reader = new CodeReader(Calibrator.DecodeSettings(product));
        string model = Path.Combine(_opt.ModelsFolder, "codenet.onnx"), enhancer = Path.Combine(_opt.ModelsFolder, "enhancer.onnx");
        if (File.Exists(model)) reader.LoadModel(model);
        if (File.Exists(enhancer)) reader.LoadEnhancer(enhancer);
        var grading = Grading(product.ProductCode);
        grading.ApplyTo(reader, GradingFolder);

        CalibrationResult result;
        CalibrationShot shot;
        string from;
        if (source == SourceKind.Camera)
        {
            // last good settings, a probe frame, then exposure and gain trials on the camera
            try
            {
                result = CameraSource.Tune(product, _opt, s => reader.Decode(s.Pixels, s.Width, s.Height, s.Width), user);
            }
            catch (Exception e) when (e is FileNotFoundException or DllNotFoundException or TypeInitializationException or BadImageFormatException)
            {
                throw new InvalidOperationException("the Hikrobot camera software (MVS) is not installed on this PC: " +
                                                    "install it, or calibrate from a saved image");
            }
            shot = result.Shot!;
            from = $"camera, {result.Trials.Count} trial{(result.Trials.Count == 1 ? "" : "s")}";
        }
        else
        {
            string path = replayImage ?? Directory.GetFiles(_opt.ReplayFolder ?? throw new InvalidOperationException("no replay folder configured"), "*.bmp")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                ?? throw new InvalidOperationException("no .bmp image in the replay folder");
            var (px, w, h) = LoadGrayImage(path);
            shot = new CalibrationShot(px, w, h, 1, 0);
            var decoded = reader.Decode(px, w, h, w);
            var frame = new CalibrationFrame(decoded, w, h, 0, CalibrationSearch.CodeSaturation(shot, decoded.Codes));
            result = Calibrator.Plan(product, new[] { frame }, user);
            from = Path.GetFileName(path);
        }

        var handle = GCHandle.Alloc(shot.Pixels, GCHandleType.Pinned);
        byte[] jpeg;
        try
        {
            var (small, sw, sh, f) = Preview.Shrink(handle.AddrOfPinnedObject(), shot.Width, shot.Height, shot.Width);
            jpeg = Preview.RenderCalibration(small, sw, sh, f, result.BestDecode!, result.Proposed.Roi);
        }
        finally
        {
            handle.Free();
        }
        _log.Audit(user, "calibrate-run", $"{product.ProductCode}: {result.CodesRead}/{result.Expected} read, " +
                                          $"labels {result.LabelBrightness:0}/255, {from}");
        var print = (result.BestDecode?.Codes ?? Array.Empty<DecodedCode>())
            .Where(c => c.Quality is not null)
            .Select(c => new PrintGrade(c.Text, c.Quality!.OverallGrade, c.Quality.SymbolContrast, c.Quality.Modulation, c.Quality.XDimMm,
                                        c.Quality.Calibrated, c.Quality.Linear is not null))
            .OrderBy(g => g.Grade).ThenBy(g => g.SymbolContrast).ToList();
        return new CalibrationView(product.ProductCode, result, current, jpeg, from) { Print = print, Grading = grading };
    }

    // ---- grading calibration (verifier-style print grades), per product beside the station's data
    private string GradingFolder => Path.Combine(_opt.DataFolder, "grading");
    private string GradingPath(string productCode) => Path.Combine(GradingFolder, productCode + ".json");
    public GradingCalibration Grading(string productCode) => GradingCalibration.Load(GradingPath(productCode));

    // one frame for a grading step: the camera at the product's settings, or the chosen saved image
    private (byte[] Pixels, int Width, int Height) GradingFrame(ProductMaster product, SourceKind source, string? replayImage)
    {
        lock (_stateLock)
            if (_cts is not null) throw new InvalidOperationException("stop the line before calibrating");
        if (source == SourceKind.Camera)
        {
            try
            {
                var shot = CameraSource.CaptureBurst(product, _opt, TestBurst.Plan(product.ChildCamera, 1), intervalMs: 0, out _)[0];
                return (shot.Pixels, shot.Width, shot.Height);
            }
            catch (Exception e) when (e is FileNotFoundException or DllNotFoundException or TypeInitializationException or BadImageFormatException)
            {
                throw new InvalidOperationException("the Hikrobot camera software (MVS) is not installed on this PC");
            }
        }
        string path = replayImage is { } n ? ReplayImagePath(n) ?? throw new InvalidOperationException("no such saved image")
                                           : throw new InvalidOperationException("choose a saved image");
        return LoadGrayImage(path);
    }

    /// <summary>Flat field: a plain white card filling the view, under the line's lighting.</summary>
    public GradingCalibration CalibrateWhiteCard(string productCode, SourceKind source, string? replayImage, string user)
    {
        var product = _products.Get(productCode) ?? throw new InvalidOperationException($"product {productCode} not found");
        var (px, w, h) = GradingFrame(product, source, replayImage);
        using (var probe = new CodeReader(Calibrator.DecodeSettings(product))) probe.SetFlatField(px, w, h, w); // throws when not a white card
        var g = Grading(productCode);
        string file = productCode + "_white.bmp";
        Directory.CreateDirectory(GradingFolder);
        BmpImage.SaveGray(Path.Combine(GradingFolder, file), px, w, h);
        g.WhiteCardFile = file;
        g.UpdatedUtc = DateTime.UtcNow;
        g.UpdatedBy = user;
        g.Save(GradingPath(productCode));
        _log.Audit(user, "grading-white-card", productCode);
        return g;
    }

    /// <summary>Reflectance: a card with a dark and a light patch of known reflectance (%) filling the middle of the view.</summary>
    public GradingCalibration CalibrateReflectance(string productCode, SourceKind source, string? replayImage, float reflDarkPct, float reflLightPct, string user)
    {
        var product = _products.Get(productCode) ?? throw new InvalidOperationException($"product {productCode} not found");
        var (px, w, h) = GradingFrame(product, source, replayImage);
        var g = Grading(productCode);
        using var reader = new CodeReader(Calibrator.DecodeSettings(product));
        g.ApplyTo(reader, GradingFolder); // the flat field first, if set
        var (dark, light) = reader.MeasureCard(px, w, h, w);
        reader.SetReflectance(dark, light, reflDarkPct, reflLightPct); // throws on impossible values
        g.GreyDark = dark;
        g.GreyLight = light;
        g.ReflDarkPct = reflDarkPct;
        g.ReflLightPct = reflLightPct;
        g.UpdatedUtc = DateTime.UtcNow;
        g.UpdatedBy = user;
        g.Save(GradingPath(productCode));
        _log.Audit(user, "grading-reflectance", $"{productCode}: grey {dark:0}/{light:0} = {reflDarkPct:0.#}%/{reflLightPct:0.#}%");
        return g;
    }

    /// <summary>Scale and lens: a chessboard lying on the case's top (inner corners, square side in mm).</summary>
    public GradingCalibration CalibrateChessboard(string productCode, SourceKind source, string? replayImage, int cols, int rows, float squareMm, string user)
    {
        var product = _products.Get(productCode) ?? throw new InvalidOperationException($"product {productCode} not found");
        var (px, w, h) = GradingFrame(product, source, replayImage);
        var lens = CodeReader.CalibrateChessboard(new[] { px }, w, h, w, cols, rows, squareMm)
                   ?? throw new InvalidOperationException($"no {cols} x {rows} chessboard found: count the inner corners, and fill the view evenly lit");
        var g = Grading(productCode);
        g.Lens = lens;
        g.UpdatedUtc = DateTime.UtcNow;
        g.UpdatedBy = user;
        g.Save(GradingPath(productCode));
        _log.Audit(user, "grading-scale", $"{productCode}: {lens.MmPerPx:0.0000} mm/px");
        return g;
    }

    /// <summary>Saves a calibration as a new version of the product (top camera settings only).</summary>
    /// <summary>Saved images the Calibrate page can use in Replay mode.</summary>
    public IReadOnlyList<string> ReplayImages() =>
        _opt.ReplayFolder is { } f && Directory.Exists(f)
            ? Directory.GetFiles(f, "*.bmp").Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    public string? ReplayImagePath(string name) =>
        _opt.ReplayFolder is { } f && !name.Contains('/') && !name.Contains('\\') && name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(f, name) : null;

    public ProductMaster ApplyCalibration(string productCode, CameraSettings proposed, string user, string? approvedBy)
    {
        lock (_stateLock)
            if (_cts is not null) throw new InvalidOperationException("stop the line before applying a calibration");
        var product = _products.Get(productCode) ?? throw new InvalidOperationException($"product {productCode} not found");
        if (product.Mode == LearningMode.Locked && approvedBy is null)
            throw new InvalidOperationException("this product is locked: applying a calibration needs a QA e-signature");
        var old = product.ChildCamera;
        product.ChildCamera = proposed;
        product.ApprovedBy = product.Mode == LearningMode.Locked ? approvedBy : null;
        var saved = _products.Save(product, user);
        var t = LightTiming.For(proposed);
        _log.Audit(user, "calibrate", $"{saved.ProductCode} v{saved.Version}: exposure {old.ExposureUs:0} -> {proposed.ExposureUs:0} us, " +
                                      $"gain {old.GainDb:0.0} -> {proposed.GainDb:0.0} dB, " +
                                      $"crop {(proposed.Roi is { } r ? $"{r.Width}x{r.Height} at {r.X},{r.Y}" : "none")}, " +
                                      $"light on {t.LightOnAtUs:0}-{t.LightOffAtUs:0} us" + (approvedBy is null ? "" : $", {approvedBy}"));
        return saved;
    }

    // saved images for replay and calibration: BMP directly; PNG / JPEG (also when saved with a
    // .bmp name) through SkiaSharp
    private static (byte[] Pixels, int Width, int Height) LoadGrayImage(string path)
    {
        try
        {
            return BmpImage.LoadGray(path);
        }
        catch (InvalidDataException)
        {
            using var bmp = SkiaSharp.SKBitmap.Decode(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)}: not an image");
            var gray = new byte[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * bmp.Width + x] = (byte)((c.Red + c.Green + c.Blue) / 3);
                }
            return (gray, bmp.Width, bmp.Height);
        }
    }

    // frames: all the case's frames when it had several (pixels = the last of them)
    private unsafe void KeepHardCase(ProductMaster product, ReaderSettings s, bool ok, bool complete, IReadOnlyList<string> reasons,
        IntPtr pixels, int w, int h, int stride, byte[][]? frames)
    {
        var last = new ReadOnlySpan<byte>((byte*)pixels, checked((int)((long)stride * (h - 1) + w)));
        HardCases.Keep(_hardCases, product, s, ok, complete, reasons, last, w, h, stride, frames, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}");
    }

    /// <summary>
    /// Takes `count` frames of the case under the camera by software trigger (line stopped) and
    /// saves them as test frames for the multi-frame reader: data/test-frames/&lt;product&gt;/
    /// &lt;stamp&gt;_f1.bmp, _f2.bmp ... with a .json beside (format, expected count, each frame's
    /// exposure and gain), the naming the bench reads with --frames N. The frames vary in
    /// brightness around the calibrated setting (1x, 0.6x, 1.6x, ...: <see cref="TestBurst"/>), so
    /// glare and faint codes each get a usable frame. Returns the folder.
    /// </summary>
    public string SaveTestFrames(string productCode, int count, string user)
    {
        lock (_stateLock)
            if (_cts is not null) throw new InvalidOperationException("stop the line before taking test frames");
        var product = _products.Get(productCode) ?? throw new InvalidOperationException($"product {productCode} not found");
        count = Math.Clamp(count, 1, 8);
        IReadOnlyList<(byte[] Pixels, int Width, int Height, double TimeMs)> shots;
        IReadOnlyList<BurstShot> applied;
        try
        {
            shots = CameraSource.CaptureBurst(product, _opt, TestBurst.Plan(product.ChildCamera, count), intervalMs: 80, out applied);
        }
        catch (Exception e) when (e is FileNotFoundException or DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            throw new InvalidOperationException("the Hikrobot camera software (MVS) is not installed on this PC");
        }
        string dir = Path.Combine(_opt.DataFolder, "test-frames", product.ProductCode);
        Directory.CreateDirectory(dir);
        string name = $"{DateTime.Now:yyyyMMdd_HHmmss}";
        for (int k = 0; k < shots.Count; k++)
            BmpImage.SaveGray(Path.Combine(dir, $"{name}_f{k + 1}.bmp"), shots[k].Pixels, shots[k].Width, shots[k].Height);
        var s = Calibrator.DecodeSettings(product);
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(new
        {
            format = s.Format == CodeFormat.DataMatrix ? "dm" : "qr", expected = s.ExpectedCount, frames = shots.Count, product = product.ProductCode,
            exposure_us = product.ChildCamera.ExposureUs, motion = product.ChildCamera.Motion.ToString(),
            frame_exposure_us = applied.Select(a => a.ExposureUs).ToArray(),
            frame_gain_db = applied.Select(a => a.GainDb).ToArray(),
            frame_brightness = applied.Select(a => MathF.Round(a.Brightness * 100) / 100).ToArray(),
        }));
        _log.Audit(user, "test-frames", $"{product.ProductCode}, {shots.Count} frames at " +
            string.Join(" / ", applied.Select(a => $"{a.Brightness:0.##}x")) + $" -> {name}");
        return dir;
    }

    public void Dispose()
    {
        Stop("system");
        _reader?.Dispose();
    }
}
