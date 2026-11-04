using VisionStudio.Remote;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

/// <summary>The "Runtime" section of vs-runtime.json (or --Runtime:Key=value on the command line).</summary>
public sealed class RuntimeOptions
{
    /// <summary>Local endpoint: "pipe:name" or a Unix socket path; empty for the default
    /// (\\.\pipe\vs-runtime on Windows, $TMPDIR/vs-runtime.sock elsewhere, or VS_RUNTIME_ENDPOINT).</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>TCP port for plant software and browsers on the network; 0 = no network listener.
    /// Every call over the network needs a token.</summary>
    public int NetworkPort { get; set; }

    /// <summary>TCP port of the browser dashboard (Hybrid 4, UI-UX-SPEC.md 4.17): view-only pages over
    /// HTTPS with the same certificate; 0 = no dashboard. Never the same as <see cref="NetworkPort"/>.</summary>
    public int WebPort { get; set; }

    /// <summary>An Operator sees the shift reports on the browser dashboard ("My shift", UI-UX-SPEC.md
    /// 4.11; requirements 6.4 WEB-02); never the batch reports. Off: an Operator has no Reports page.</summary>
    public bool WebOperatorShiftReports { get; set; }

    /// <summary>The TLS certificate (.pfx) for the network listener; without one the network
    /// listener does not open (PRD section 12: remote access needs TLS).</summary>
    public string CertificateFile { get; set; } = "";

    /// <summary>The certificate's password comes from this environment variable, never from the file.</summary>
    public string CertificatePasswordVariable { get; set; } = "VS_RUNTIME_CERT_PASSWORD";

    /// <summary>Plain HTTP/2 on the network port, for tests on this PC only. Never on a line.</summary>
    public bool AllowInsecureNetwork { get; set; }

    /// <summary>The source Start uses when the caller names none: "camera" or "replay". Empty:
    /// camera on Windows, replay elsewhere (ARCHITECTURE.md section 2.4).</summary>
    public string Source { get; set; } = "";

    /// <summary>A folder of 8-bit BMP frames played as camera "sim" through the frame intake, for
    /// trying the camera path on a PC without cameras. A job camera with stored pictures of its
    /// own (its test folder) plays those instead. Empty: no simulator.</summary>
    public string SimulatorFolder { get; set; } = "";

    /// <summary>A fixed time from a simulator trigger to its frame, in ms. Not set: the time the line's camera
    /// would take, the job camera's exposure plus the frame over <see cref="SimulatorLink"/> (RT-SF-01).</summary>
    public int? SimulatorFrameMs { get; set; }

    /// <summary>The line's camera link the simulator times its frames by: "GigE" (100 MB/s) or "USB3" (380 MB/s).</summary>
    public string SimulatorLink { get; set; } = "GigE";

    /// <summary>The remote support relay's link address (REQ-11, PROT-26), e.g. wss://support.sundaramtech.com/link.
    /// The runtime dials it (outbound only) and serves the browser dashboard to Sundaram service engineers
    /// through it. Empty: no remote support. Needs <see cref="WebPort"/> and <see cref="SupportMachine"/>; the token
    /// is the commissioning secret VS_SUPPORT_LINK_TOKEN, never in this file.</summary>
    public string SupportRelay { get; set; } = "";

    /// <summary>This line PC's machine number on the relay (support.sundaramtech.com/&lt;machine&gt;).</summary>
    public string SupportMachine { get; set; } = "";

    /// <summary>A signed-in session ends after this long without a call.</summary>
    public TimeSpan SessionIdle { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Vision Studio signs its user out after this long without a touch or key, back to
    /// Operator (UI-UX-SPEC.md 4.5, plant setting). The app measures it; WhoAmI tells it.</summary>
    public TimeSpan StudioIdleSignOut { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The speed the line is set to run at, in parts per minute, for the Run screen's charts
    /// (UI-UX-SPEC.md 4.1); 0 = not set (no line drawn).</summary>
    public double LineSpeedPpm { get; set; }

    /// <summary>The time one part may take, in ms; 0 = from the line speed (60000 / LineSpeedPpm).</summary>
    public double CycleBudgetMs { get; set; }

    /// <summary>The picture check during a run (requirements AS-13a): one picture in this many of each camera is
    /// compared with the camera's taught picture, off the inspecting thread; 0 = off. Advice only.</summary>
    public int PictureCheckEvery { get; set; } = 50;

    /// <summary>The cycle budget in ms: <see cref="CycleBudgetMs"/>, else from the line speed; 0 when neither is set.</summary>
    public double CycleBudget() =>
        CycleBudgetMs > 0 ? CycleBudgetMs : LineSpeedPpm > 0 ? 60000.0 / LineSpeedPpm : 0;

    public RuntimeEndpoint LocalEndpoint() =>
        string.IsNullOrWhiteSpace(Endpoint) ? RuntimeEndpoint.Default() : RuntimeEndpoint.Parse(Endpoint);

    public V1.Source DefaultSource() => Source.Trim().ToLowerInvariant() switch
    {
        "camera" => V1.Source.Camera,
        "replay" => V1.Source.Replay,
        _ => OperatingSystem.IsWindows() ? V1.Source.Camera : V1.Source.Replay,
    };
}
