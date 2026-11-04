using System.Diagnostics;
using System.Text.Json;
using Xunit;

/// <summary>vs-runtime self-test --json: the report first-run setup adds to the install report.
/// Runs the program as the line PC does, in its own process.</summary>
public sealed class SelfTestTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-selftest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Writes_every_check_to_json_and_fails_a_job_camera_that_is_not_there()
    {
        var jobs = Path.Combine(_dir, "jobs");
        Directory.CreateDirectory(jobs);
        File.WriteAllText(Path.Combine(jobs, "two.json"),
            """{"Name":"two","Cameras":[{"Name":"Camera 1","Serial":"NOT-A-CAMERA-1"},{"Name":"Camera 2","Serial":"NOT-A-CAMERA-2"}]}""");
        var json = Path.Combine(_dir, "out", "selftest.json");

        var dll = Path.Combine(AppContext.BaseDirectory, "vs-runtime.dll");
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { dll, "self-test", "--json", json, $"--Studio:JobsFolder={jobs}", $"--Studio:DataFolder={Path.Combine(_dir, "data")}" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);

        Assert.Equal(1, p.ExitCode);   // the job's cameras are not there: never a pass
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        Assert.Equal("FAIL", doc.RootElement.GetProperty("overall").GetString());
        var results = doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => (Check: r.GetProperty("Check").GetString()!, Verdict: r.GetProperty("Verdict").GetString()!, Group: r.GetProperty("Group").GetString()!))
            .ToList();
        Assert.All(results, r => Assert.Equal("Self-test", r.Group));
        Assert.Contains(results, r => r.Check == "data folder" && r.Verdict == "PASS");
        Assert.Contains(results, r => r.Check == "job two, Camera 2" && r.Verdict == "FAIL");
        Assert.Contains("self-test FAILED", await stdout);
    }
}
