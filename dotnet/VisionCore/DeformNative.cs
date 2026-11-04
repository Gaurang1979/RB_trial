using System;
using System.Runtime.InteropServices;

namespace VisionCore;

public static class DeformNative
{
    private const string Lib = "vs_deform";

    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public int StructSize;
        public int PyramidLevels;
        public int MaxIterationsPerLevel;
        public float SmoothnessWeight;
        public float ConvergenceEpsilon;
        public float RuptureStrainThreshold;
        public float CrumpleBendingThreshold;
        public float MinAlignmentScore;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public int StructSize;
        public float FinalAlignmentScore;
        public float MeanDisplacementPx;
        public float MaxDisplacementPx;
        public float MaxAreaStretch;
        public float MaxAreaCompression;
        public float TotalBendingEnergy;
        public int IsFilmRuptured;
        public int IsExcessiveCrumple;
        public int IsMatchOk;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vs_deform_default_params(ref Params p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_deform_estimate(
        IntPtr testImg,
        int testW,
        int testH,
        int testStride,
        IntPtr refImg,
        int refW,
        int refH,
        int refStride,
        ref Params p,
        IntPtr meshDx,
        IntPtr meshDy,
        int meshCols,
        int meshRows,
        ref Stats stats);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_deform_unwarp(
        IntPtr testImg,
        int testW,
        int testH,
        int testStride,
        IntPtr meshDx,
        IntPtr meshDy,
        int meshCols,
        int meshRows,
        IntPtr unwarpedOut,
        int outW,
        int outH,
        int outStride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int vs_deform_analyze_strain(
        IntPtr meshDx,
        IntPtr meshDy,
        int meshCols,
        int meshRows,
        float cellW,
        float cellH,
        ref Params p,
        ref Stats stats);
}
