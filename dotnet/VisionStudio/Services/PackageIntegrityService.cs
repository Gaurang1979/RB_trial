using Microsoft.Extensions.Options;
using VisionCore.Integrity;

namespace VisionStudio.Services;

/// <summary>
/// Runs the package integrity check once at start and holds the result for the rest of the software
/// (docs/vision-system/PACKAGE-PROTECTION.md). The check reads the signed manifest and the public key
/// shipped next to the program and confirms every shipped file is present and unchanged.
///
/// Line safety (requirements section 12): when the check fails the software must not run altered code.
/// <see cref="Station"/> refuses to start production (the line is told and parts stay NG), and the
/// runtime raises an alarm — never a silent wrong read. The check is only done when
/// <see cref="Required"/> (Studio:IntegrityRequired), so a plain source build without a signed
/// manifest still runs for development.
/// </summary>
public sealed class PackageIntegrityService
{
    private readonly string _baseDir;
    private readonly ILogger<PackageIntegrityService>? _log;
    private readonly Lazy<IntegrityResult> _result;

    public PackageIntegrityService(IOptions<StudioOptions> opt, ILogger<PackageIntegrityService>? log = null)
        : this(AppContext.BaseDirectory, opt.Value.IntegrityRequired, log) { }

    /// <summary>For tests and tools: check a chosen folder.</summary>
    public PackageIntegrityService(string baseDir, bool required, ILogger<PackageIntegrityService>? log = null)
    {
        _baseDir = baseDir;
        Required = required;
        _log = log;
        _result = new Lazy<IntegrityResult>(Run);
    }

    /// <summary>Off on development and demo PCs (Studio:IntegrityRequired).</summary>
    public bool Required { get; }

    /// <summary>The one check's result (run on first use, then cached).</summary>
    public IntegrityResult Result => _result.Value;

    /// <summary>The software may run production: not required here, or every shipped file is as signed.</summary>
    public bool Ok => !Required || Result.Ok;

    /// <summary>A short reason to show when it is not OK.</summary>
    public string Detail => Result.Detail;

    private IntegrityResult Run()
    {
        var keyPath = Path.Combine(_baseDir, PackageIntegrity.PublicKeyFile);
        var manPath = Path.Combine(_baseDir, PackageIntegrity.ManifestFile);
        var key = File.Exists(keyPath) ? File.ReadAllText(keyPath) : null;
        var man = File.Exists(manPath) ? File.ReadAllText(manPath) : null;
        var r = PackageIntegrity.Check(_baseDir, key, man);
        if (!Required)
            _log?.LogInformation("Package integrity: {State} (not enforced on this PC)", r.State);
        else if (r.Ok)
            _log?.LogInformation("Package integrity: {Detail}", r.Detail);
        else
            _log?.LogError("Package integrity FAILED: {Detail} The line will not run altered software.", r.Detail);
        return r;
    }
}
