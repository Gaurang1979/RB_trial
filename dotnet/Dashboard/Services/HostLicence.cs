using VisionCore.Licensing;

namespace BarcodeReading.Dashboard.Services;

/// <summary>
/// The station's hold on the licence content key (requirements 18.3, ENC-HOST-01). <see cref="Keys"/> is null on a
/// development PC (no <see cref="StationOptions.LicenceFolder"/>): nothing is required and artifacts are read in the
/// clear. Otherwise the station starts nothing while the key is not available.
/// </summary>
public sealed record HostLicence(StoredLicenceKeySource? Keys)
{
    /// <summary>Null when an inspection may start; otherwise why not (no key, not licensed, offline too long).</summary>
    public string? InspectionBlockedReason() => Keys?.InspectionBlockedReason();
}
