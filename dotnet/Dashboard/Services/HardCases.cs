using StationGuard;

namespace BarcodeReading.Dashboard.Services;

/// <summary>The PC's health as the station sees it: the guard's level and what it means for the hard cases.</summary>
public sealed record StationHealth(Pressure Level, string? Warning, bool ImagesEnabled, long HardCasesSaved, long HardCasesSkipped,
    bool RestartRecommended);

/// <summary>
/// Counts every case in the <see cref="NgImageStore"/> and offers the frames of a short case (not every
/// code read) for the retraining agent. The store copies a frame only when it will keep it and writes it on
/// its own thread, so the inspection thread never waits for the disk. The verdict is decided before this
/// is called and nothing here changes it: a case the store cannot keep is still NG, only not saved.
/// </summary>
public static class HardCases
{
    /// <summary>The camera name of a one-frame case's file (&lt;image id&gt;_top.bmp).</summary>
    public const string Camera = "top";

    /// <summary>Why a case was rejected, for the NG counts; empty for an OK case.</summary>
    public static IReadOnlyList<string> Reasons(bool complete, bool parentMissing, bool parentInvalid, bool duplicate, bool extra = false)
    {
        var r = new List<string>(5);
        if (!complete) r.Add("codes_missing");
        if (extra) r.Add("codes_extra");
        if (parentMissing) r.Add("no_parent");
        if (parentInvalid) r.Add("parent_invalid");
        if (duplicate) r.Add("duplicate");
        return r;
    }

    /// <summary>
    /// Counts the case and, when it was short, offers its frames: one file <c>&lt;imageId&gt;_top.bmp</c>, or
    /// <c>&lt;imageId&gt;_f1.bmp</c>, <c>_f2.bmp</c> ... for a case of several frames (the bench's --frames naming), each
    /// with a .json holding the format and expected count the retraining agent reads. <paramref name="last"/> is
    /// the one frame (or the case's last one), <paramref name="frames"/> all of them when there are several.
    /// Never throws and never waits for the disk; returns how many frames will be saved.
    /// </summary>
    public static int Keep(NgImageStore store, ProductMaster product, ReaderSettings s, bool ok, bool complete,
        IReadOnlyList<string> reasons, ReadOnlySpan<byte> last, int w, int h, int stride, byte[][]? frames, string imageId)
    {
        try
        {
            store.CountPart(ok, reasons);
            // only short cases teach the reader; a case rejected for its parent or a duplicate read every code
            if (complete) return 0;
            var meta = new Dictionary<string, object?>
            {
                ["format"] = s.Format == CodeFormat.DataMatrix ? "dm" : "qr",
                ["expected"] = s.ExpectedCount,
                ["exposure_us"] = product.ChildCamera.ExposureUs,
                ["product"] = product.ProductCode,
            };
            if (frames is not { Length: > 1 })
                return store.KeepImage(product.ProductCode, ok, reasons, last, w, h, stride, imageId, Camera, meta) ? 1 : 0;
            int kept = 0;
            for (int k = 0; k < frames.Length; k++)
                if (store.KeepImage(product.ProductCode, ok, reasons, frames[k], w, h, stride, imageId, $"f{k + 1}",
                        new Dictionary<string, object?>(meta) { ["frame"] = k + 1, ["frames"] = frames.Length }))
                    kept++;
            return kept;
        }
        catch (Exception)
        {
            return 0; // the store never throws; this only makes sure the case's verdict is never lost to it
        }
    }

    /// <summary>The station's health from the guard and the store, with the warning the Live page shows (null when all is well).</summary>
    public static StationHealth Health(GuardStatus guard, NgCounts counts, bool imagesEnabled)
    {
        var notes = new List<string>(3);
        if (guard.Level == Pressure.Critical)
            notes.Add($"PC under critical pressure ({guard.Reason}): no hard-case images are saved. Inspection and verdicts continue.");
        else if (guard.Level == Pressure.High)
            notes.Add($"PC under pressure ({guard.Reason}): only a few hard-case images are saved. Inspection and verdicts continue.");
        if (!imagesEnabled)
            notes.Add("The hard-case folder cannot be used: no images are saved (cases are still counted).");
        long failed = counts.Skipped.GetValueOrDefault(SkipReason.Disk) + counts.Skipped.GetValueOrDefault(SkipReason.WriteError);
        if (imagesEnabled && failed > 0)
            notes.Add($"{failed} hard-case image(s) could not be written (disk full or not writable).");
        if (guard.RestartRecommended)
            notes.Add("The PC stays under pressure after every clean-up step: restart the station between batches.");
        long skipped = counts.Skipped.Where(kv => kv.Key != SkipReason.Policy).Sum(kv => kv.Value);
        return new StationHealth(guard.Level, notes.Count == 0 ? null : string.Join(" ", notes), imagesEnabled, counts.Saved, skipped,
            guard.RestartRecommended);
    }
}
