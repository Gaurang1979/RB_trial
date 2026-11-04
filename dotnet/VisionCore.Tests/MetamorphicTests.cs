using System;
using Xunit;

namespace VisionCore.Tests;

public class MetamorphicTests
{
    [Fact]
    public void ApplyTransform_RotAndNoise_PerturbsImage()
    {
        const int w = 80;
        const int h = 80;
        byte[] src = new byte[w * h];
        Array.Fill(src, (byte)200);

        for (int y = 30; y < 50; y++)
        {
            for (int x = 30; x < 50; x++)
            {
                src[y * w + x] = 30;
            }
        }

        // Apply brightness boost + sensor noise
        byte[] transformed = MetamorphicEngine.ApplyTransform(
            src, w, h,
            rotAngleDeg: 1.5f,
            gainGamma: 1.05f,
            biasBeta: 20.0f,
            noiseSigma: 5.0f);

        Assert.NotNull(transformed);
        Assert.Equal(w * h, transformed.Length);

        // Center pixel (originally 30) should now be ~51 + noise
        int centerVal = transformed[40 * w + 40];
        Assert.InRange(centerVal, 40, 70);
    }

    [Fact]
    public void RunFuzzingSuite_RobustBlobCentroid_PassesMetamorphicCertification()
    {
        const int w = 100;
        const int h = 100;
        byte[] img = new byte[w * h];
        Array.Fill(img, (byte)220);

        // Circular dark marker centered at (55, 45)
        float trueCx = 55.0f;
        float trueCy = 45.0f;
        float radius = 12.0f;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float d = MathF.Sqrt((x - trueCx) * (x - trueCx) + (y - trueCy) * (y - trueCy));
                if (d < radius)
                {
                    img[y * w + x] = 30;
                }
            }
        }

        VisionEvalDelegate robustDelegate = (testImg, imgW, imgH) =>
        {
            double m00 = 0, m10 = 0, m01 = 0;
            int darkCount = 0;

            for (int y = 0; y < imgH; y++)
            {
                for (int x = 0; x < imgW; x++)
                {
                    byte v = testImg[y * imgW + x];
                    if (v < 128)
                    {
                        double weight = (128.0 - v) / 128.0;
                        m00 += weight;
                        m10 += weight * x;
                        m01 += weight * y;
                        darkCount++;
                    }
                }
            }

            if (m00 > 10.0)
            {
                float px = (float)(m10 / m00);
                float py = (float)(m01 / m00);
                float conf = (float)Math.Min(1.0, m00 / 300.0);
                bool ok = darkCount > 100;
                return (conf, px, py, ok);
            }
            return (0.1f, imgW * 0.5f, imgH * 0.5f, false);
        };

        var report = MetamorphicEngine.RunFuzzingSuite(
            img, w, h,
            robustDelegate,
            maxRotationDeg: 3.0f,
            maxAllowedJitterPx: 0.75f);

        Assert.True(report.IsRobust, "Robust vision detector must pass metamorphic certification");
        Assert.Equal(0, report.VerdictFlips);
        Assert.True(report.TotalEvaluations >= 15);
        Assert.True(report.MaxPositionalJitterPx <= 0.75f, $"Jitter was {report.MaxPositionalJitterPx}");
        Assert.True(report.MinObservedConfidence >= 0.70f);
    }

    [Fact]
    public void RunFuzzingSuite_BrittleThreshold_FlagsMetamorphicViolation()
    {
        const int w = 60;
        const int h = 60;
        byte[] img = new byte[w * h];
        Array.Fill(img, (byte)200);

        for (int y = 20; y < 40; y++)
        {
            for (int x = 20; x < 40; x++)
            {
                img[y * w + x] = 40;
            }
        }

        // Brittle detector that breaks on slight photometric or noise variation
        VisionEvalDelegate brittleDelegate = (testImg, imgW, imgH) =>
        {
            int cx = imgW / 2;
            int cy = imgH / 2;
            byte centerVal = testImg[cy * imgW + cx];

            if (centerVal > 44)
            {
                return (0.3f, 0.0f, 0.0f, false); // Verdict flip!
            }
            return (0.95f, (float)cx, (float)cy, true);
        };

        var report = MetamorphicEngine.RunFuzzingSuite(img, w, h, brittleDelegate);

        Assert.False(report.IsRobust, "Brittle threshold algorithm must fail metamorphic certification");
        Assert.True(report.VerdictFlips > 0);
        Assert.False(string.IsNullOrEmpty(report.FailureReason));
    }

    [Fact]
    public void MetamorphicTool_NominalImage_PassesCertification()
    {
        const int w = 80;
        const int h = 80;
        byte[] px = new byte[w * h];
        Array.Fill(px, (byte)210);

        // Circular dark feature in center
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float d = MathF.Sqrt((x - 40) * (x - 40) + (y - 40) * (y - 40));
                if (d < 10)
                {
                    px[y * w + x] = 20;
                }
            }
        }

        var cfg = new MetamorphicToolConfig
        {
            Name = "MetaTool1",
            MaxRotationDeg = 2.0f,
            RotationSteps = 5,
            MaxIllumGainDelta = 0.08f,
            IllumSteps = 3,
            MaxAllowedJitterPx = 0.80f,
            MinAcceptableConfidence = 0.50f,
            FailOnNonRobust = true
        };

        using var tool = new MetamorphicTool(cfg);
        var img = new GrayImage(px, w, h);
        var batch = new BatchFields();
        var result = tool.Run(img, batch);

        Assert.True(result.Pass, $"Expected pass but failed: {result.Summary}");
        Assert.Contains("metamorphic.evaluations", result.Values.Keys);
        Assert.True(result.Values["metamorphic.evaluations"] > 0);
        Assert.Equal(0, result.Values["metamorphic.verdict_flips"]);
        Assert.Equal(1.0, result.Values["metamorphic.is_robust"]);
        Assert.NotEmpty(result.Graphics);
    }

    [Fact]
    public void MetamorphicTool_InvalidImage_FailsSafe()
    {
        var cfg = new MetamorphicToolConfig { Name = "InvalidMetaTool" };
        using var tool = new MetamorphicTool(cfg);
        var img = new GrayImage(Array.Empty<byte>(), 0, 0);
        var batch = new BatchFields();
        var result = tool.Run(img, batch);

        Assert.False(result.Pass, "Empty/invalid image must fail safe");
        Assert.Equal("invalid_input", result.Explain);
    }
}
