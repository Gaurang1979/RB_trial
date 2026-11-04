using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public delegate (float confidence, float outX, float outY, bool isOk) VisionEvalDelegate(
    byte[] img,
    int width,
    int height);

public sealed record MetamorphicReport(
    int TotalEvaluations,
    int VerdictFlips,
    float MaxPositionalJitterPx,
    float MeanPositionalJitterPx,
    float MinObservedConfidence,
    float MaxObservedConfidence,
    bool IsRobust,
    string FailureReason);

/// <summary>
/// Metamorphic Robustness & Fuzzing Test Suite (Tool H, Roadmap Priority 2 / AG-10).
/// Verifies mathematical metamorphic relations: continuous rotational stability,
/// photometric monotonicity, affine scale invariance, additive sensor noise resilience,
/// and quiet-zone monotonicity to certify zero field regressions on factory lines.
/// </summary>
public static class MetamorphicEngine
{
    /// <summary>
    /// Synthesize a perturbed metamorphic variant of an image.
    /// </summary>
    public static byte[] ApplyTransform(
        byte[] src,
        int srcW,
        int srcH,
        float rotAngleDeg = 0.0f,
        float scale = 1.0f,
        float shiftX = 0.0f,
        float shiftY = 0.0f,
        float gainGamma = 1.0f,
        float biasBeta = 0.0f,
        float noiseSigma = 0.0f,
        int quietZonePadPx = 0)
    {
        if (src == null) throw new ArgumentNullException(nameof(src));
        if (srcW <= 0 || srcH <= 0) throw new ArgumentOutOfRangeException(nameof(srcW));

        byte[] dst = new byte[srcW * srcH];
        var t = new MetamorphicNative.Transform
        {
            RotAngleDeg = rotAngleDeg,
            Scale = scale,
            ShiftX = shiftX,
            ShiftY = shiftY,
            GainGamma = gainGamma,
            BiasBeta = biasBeta,
            NoiseSigma = noiseSigma,
            QuietZonePadPx = quietZonePadPx
        };

        GCHandle hSrc = GCHandle.Alloc(src, GCHandleType.Pinned);
        GCHandle hDst = GCHandle.Alloc(dst, GCHandleType.Pinned);

        try
        {
            int rc = MetamorphicNative.vs_metamorphic_apply_transform(
                hSrc.AddrOfPinnedObject(),
                srcW, srcH, srcW,
                ref t,
                hDst.AddrOfPinnedObject(),
                srcW, srcH, srcW);

            if (rc != 0) throw new InvalidOperationException($"Metamorphic transform failed with error {rc}.");
            return dst;
        }
        finally
        {
            if (hSrc.IsAllocated) hSrc.Free();
            if (hDst.IsAllocated) hDst.Free();
        }
    }

    /// <summary>
    /// Execute comprehensive metamorphic robustness fuzzing on a managed vision callback.
    /// </summary>
    public static MetamorphicReport RunFuzzingSuite(
        byte[] baselineImg,
        int width,
        int height,
        VisionEvalDelegate evalDelegate,
        float maxRotationDeg = 3.0f,
        int rotationSteps = 7,
        float maxIllumGainDelta = 0.10f,
        int illumSteps = 5,
        float maxScaleDelta = 0.05f,
        int scaleSteps = 5,
        float maxNoiseSigma = 5.0f,
        int noiseSteps = 4,
        int maxQuietZonePx = 8,
        float maxAllowedJitterPx = 0.75f,
        float minAcceptableConfidence = 0.60f)
    {
        if (baselineImg == null) throw new ArgumentNullException(nameof(baselineImg));
        if (evalDelegate == null) throw new ArgumentNullException(nameof(evalDelegate));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        var cfg = new MetamorphicNative.Config();
        MetamorphicNative.vs_metamorphic_default_config(ref cfg);
        cfg.MaxRotationDeg = maxRotationDeg;
        cfg.RotationSteps = rotationSteps;
        cfg.MaxIllumGainDelta = maxIllumGainDelta;
        cfg.IllumSteps = illumSteps;
        cfg.MaxScaleDelta = maxScaleDelta;
        cfg.ScaleSteps = scaleSteps;
        cfg.MaxNoiseSigma = maxNoiseSigma;
        cfg.NoiseSteps = noiseSteps;
        cfg.MaxQuietZonePx = maxQuietZonePx;
        cfg.MaxAllowedJitterPx = maxAllowedJitterPx;
        cfg.MinAcceptableConfidence = minAcceptableConfidence;

        var report = new MetamorphicNative.Report();
        GCHandle hBase = GCHandle.Alloc(baselineImg, GCHandleType.Pinned);

        // Native callback forwarding to managed delegate
        MetamorphicNative.VisionEvalCallback nativeCallback = (
            IntPtr imgPtr,
            int w,
            int h,
            int stride,
            IntPtr userData,
            ref float conf,
            ref float outX,
            ref float outY,
            ref int isOk) =>
        {
            byte[] managedBuf = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(imgPtr + y * stride, managedBuf, y * w, w);
            }

            var res = evalDelegate(managedBuf, w, h);
            conf = res.confidence;
            outX = res.outX;
            outY = res.outY;
            isOk = res.isOk ? 1 : 0;
            return 0;
        };

        try
        {
            int rc = MetamorphicNative.vs_metamorphic_run_fuzzing_suite(
                hBase.AddrOfPinnedObject(),
                width,
                height,
                width,
                ref cfg,
                nativeCallback,
                IntPtr.Zero,
                ref report);

            if (rc != 0) throw new InvalidOperationException($"Metamorphic fuzzing suite failed with error {rc}.");

            return new MetamorphicReport(
                report.TotalEvaluations,
                report.VerdictFlips,
                report.MaxPositionalJitterPx,
                report.MeanPositionalJitterPx,
                report.MinObservedConfidence,
                report.MaxObservedConfidence,
                report.IsRobust != 0,
                report.FailureReason ?? string.Empty);
        }
        finally
        {
            if (hBase.IsAllocated) hBase.Free();
            GC.KeepAlive(nativeCallback);
        }
    }
}
