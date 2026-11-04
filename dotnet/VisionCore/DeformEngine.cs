using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public sealed record DeformStats(
    float FinalAlignmentScore,
    float MeanDisplacementPx,
    float MaxDisplacementPx,
    float MaxAreaStretch,
    float MaxAreaCompression,
    float TotalBendingEnergy,
    bool IsFilmRuptured,
    bool IsExcessiveCrumple,
    bool IsMatchOk);

public sealed record DeformResult(
    byte[]? UnwarpedImage,
    float[] MeshDx,
    float[] MeshDy,
    int MeshCols,
    int MeshRows,
    DeformStats Stats);

/// <summary>
/// Non-Rigid Deformable B-Spline Mesh Matcher and Rectifier (Tool E / Priority 4).
/// Provides sub-pixel non-rigid registration and canonical rectification for wrinkled flexible
/// packaging (pouches, flow-wraps, bags, blister foils). Detects film tears and excessive crumpling.
/// </summary>
public static class DeformEngine
{
    /// <summary>
    /// Estimate non-rigid displacement field and optionally unwarp deformed test image
    /// to flat canonical template plane.
    /// </summary>
    public static DeformResult MatchAndUnwarp(
        byte[] testImg,
        int testW,
        int testH,
        byte[] refImg,
        int refW,
        int refH,
        int meshCols = 9,
        int meshRows = 9,
        int pyramidLevels = 3,
        int maxIterationsPerLevel = 15,
        float smoothnessWeight = 0.35f,
        float ruptureStrainThreshold = 0.35f,
        float crumpleBendingThreshold = 40.0f,
        float minAlignmentScore = 0.70f,
        bool outputUnwarped = true)
    {
        if (testImg == null) throw new ArgumentNullException(nameof(testImg));
        if (refImg == null) throw new ArgumentNullException(nameof(refImg));
        if (testW <= 0 || testH <= 0) throw new ArgumentOutOfRangeException(nameof(testW));
        if (refW <= 0 || refH <= 0) throw new ArgumentOutOfRangeException(nameof(refW));
        if (meshCols < 2 || meshRows < 2) throw new ArgumentOutOfRangeException(nameof(meshCols));

        var p = new DeformNative.Params();
        DeformNative.vs_deform_default_params(ref p);
        p.PyramidLevels = pyramidLevels;
        p.MaxIterationsPerLevel = maxIterationsPerLevel;
        p.SmoothnessWeight = smoothnessWeight;
        p.RuptureStrainThreshold = ruptureStrainThreshold;
        p.CrumpleBendingThreshold = crumpleBendingThreshold;
        p.MinAlignmentScore = minAlignmentScore;

        int totalNodes = meshCols * meshRows;
        float[] meshDx = new float[totalNodes];
        float[] meshDy = new float[totalNodes];
        byte[]? unwarped = outputUnwarped ? new byte[refW * refH] : null;

        var stats = new DeformNative.Stats();

        GCHandle hTest = GCHandle.Alloc(testImg, GCHandleType.Pinned);
        GCHandle hRef = GCHandle.Alloc(refImg, GCHandleType.Pinned);
        GCHandle hDx = GCHandle.Alloc(meshDx, GCHandleType.Pinned);
        GCHandle hDy = GCHandle.Alloc(meshDy, GCHandleType.Pinned);
        GCHandle hUnwarp = outputUnwarped ? GCHandle.Alloc(unwarped, GCHandleType.Pinned) : default;

        try
        {
            int rc = DeformNative.vs_deform_estimate(
                hTest.AddrOfPinnedObject(),
                testW, testH, testW,
                hRef.AddrOfPinnedObject(),
                refW, refH, refW,
                ref p,
                hDx.AddrOfPinnedObject(),
                hDy.AddrOfPinnedObject(),
                meshCols, meshRows,
                ref stats);

            if (rc != 0) throw new InvalidOperationException($"Deformable estimation failed with error {rc}.");

            if (outputUnwarped && unwarped != null)
            {
                rc = DeformNative.vs_deform_unwarp(
                    hTest.AddrOfPinnedObject(),
                    testW, testH, testW,
                    hDx.AddrOfPinnedObject(),
                    hDy.AddrOfPinnedObject(),
                    meshCols, meshRows,
                    hUnwarp.AddrOfPinnedObject(),
                    refW, refH, refW);

                if (rc != 0) throw new InvalidOperationException($"Deformable unwarping failed with error {rc}.");
            }

            var resultStats = new DeformStats(
                stats.FinalAlignmentScore,
                stats.MeanDisplacementPx,
                stats.MaxDisplacementPx,
                stats.MaxAreaStretch,
                stats.MaxAreaCompression,
                stats.TotalBendingEnergy,
                stats.IsFilmRuptured != 0,
                stats.IsExcessiveCrumple != 0,
                stats.IsMatchOk != 0);

            return new DeformResult(unwarped, meshDx, meshDy, meshCols, meshRows, resultStats);
        }
        finally
        {
            if (hTest.IsAllocated) hTest.Free();
            if (hRef.IsAllocated) hRef.Free();
            if (hDx.IsAllocated) hDx.Free();
            if (hDy.IsAllocated) hDy.Free();
            if (hUnwarp.IsAllocated) hUnwarp.Free();
        }
    }

    /// <summary>
    /// Unwarp a deformed image into flat plane using an existing displacement mesh.
    /// </summary>
    public static byte[] Unwarp(
        byte[] testImg,
        int testW,
        int testH,
        float[] meshDx,
        float[] meshDy,
        int meshCols,
        int meshRows,
        int outW,
        int outH)
    {
        if (testImg == null) throw new ArgumentNullException(nameof(testImg));
        if (meshDx == null || meshDy == null) throw new ArgumentNullException(nameof(meshDx));
        if (testW <= 0 || testH <= 0 || outW <= 0 || outH <= 0) throw new ArgumentOutOfRangeException(nameof(testW));

        byte[] unwarped = new byte[outW * outH];
        GCHandle hTest = GCHandle.Alloc(testImg, GCHandleType.Pinned);
        GCHandle hDx = GCHandle.Alloc(meshDx, GCHandleType.Pinned);
        GCHandle hDy = GCHandle.Alloc(meshDy, GCHandleType.Pinned);
        GCHandle hOut = GCHandle.Alloc(unwarped, GCHandleType.Pinned);

        try
        {
            int rc = DeformNative.vs_deform_unwarp(
                hTest.AddrOfPinnedObject(),
                testW, testH, testW,
                hDx.AddrOfPinnedObject(),
                hDy.AddrOfPinnedObject(),
                meshCols, meshRows,
                hOut.AddrOfPinnedObject(),
                outW, outH, outW);

            if (rc != 0) throw new InvalidOperationException($"Deformable unwarping failed with error {rc}.");
            return unwarped;
        }
        finally
        {
            if (hTest.IsAllocated) hTest.Free();
            if (hDx.IsAllocated) hDx.Free();
            if (hDy.IsAllocated) hDy.Free();
            if (hOut.IsAllocated) hOut.Free();
        }
    }

    /// <summary>
    /// Analyze mechanical packaging strain (area stretch, film rupture risk, crumple energy).
    /// </summary>
    public static DeformStats AnalyzeStrain(
        float[] meshDx,
        float[] meshDy,
        int meshCols,
        int meshRows,
        float cellW,
        float cellH,
        float ruptureStrainThreshold = 0.35f,
        float crumpleBendingThreshold = 40.0f)
    {
        if (meshDx == null || meshDy == null) throw new ArgumentNullException(nameof(meshDx));
        if (meshCols < 2 || meshRows < 2) throw new ArgumentOutOfRangeException(nameof(meshCols));

        var p = new DeformNative.Params();
        DeformNative.vs_deform_default_params(ref p);
        p.RuptureStrainThreshold = ruptureStrainThreshold;
        p.CrumpleBendingThreshold = crumpleBendingThreshold;

        var stats = new DeformNative.Stats();
        GCHandle hDx = GCHandle.Alloc(meshDx, GCHandleType.Pinned);
        GCHandle hDy = GCHandle.Alloc(meshDy, GCHandleType.Pinned);

        try
        {
            int rc = DeformNative.vs_deform_analyze_strain(
                hDx.AddrOfPinnedObject(),
                hDy.AddrOfPinnedObject(),
                meshCols, meshRows,
                cellW, cellH,
                ref p,
                ref stats);

            if (rc != 0) throw new InvalidOperationException($"Deformable strain analysis failed with error {rc}.");

            return new DeformStats(
                stats.FinalAlignmentScore,
                stats.MeanDisplacementPx,
                stats.MaxDisplacementPx,
                stats.MaxAreaStretch,
                stats.MaxAreaCompression,
                stats.TotalBendingEnergy,
                stats.IsFilmRuptured != 0,
                stats.IsExcessiveCrumple != 0,
                stats.IsMatchOk != 0);
        }
        finally
        {
            if (hDx.IsAllocated) hDx.Free();
            if (hDy.IsAllocated) hDy.Free();
        }
    }
}
