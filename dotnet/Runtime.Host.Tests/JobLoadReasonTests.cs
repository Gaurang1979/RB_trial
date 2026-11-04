using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionCore;
using VisionCore.Plc;
using VisionStudio.Services;
using Xunit;

/// <summary>A job change by number from the PLC (Station.LoadJob): a job that cannot be loaded answers
/// JobNotFound with the reason, never CameraNotFound, and the line keeps the job it had.</summary>
public sealed class JobLoadReasonTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;

    public async Task InitializeAsync() => _f = await RuntimeFixture.StartAsync(f =>
    {
        var jobs = Path.Combine(f.Folder, "jobs");
        // reads, but cannot run: a locate tool with no pattern taught
        var unteached = new Job
        {
            Name = "Unteached",
            Number = 12,
            Cameras = [new CameraConfig { Name = "Top", TestFolder = f.FramesFolder }],
            Tools = [new LocateToolConfig { Name = "Find label" }],
        };
        File.WriteAllText(Path.Combine(jobs, "Unteached.json"), JsonSerializer.Serialize(unteached, JobStore.Json));
        // does not read at all
        File.WriteAllText(Path.Combine(jobs, "Broken.json"), "{ \"Name\": \"Broken\", \"Number\": 13, ");
    });

    public async Task DisposeAsync() => await _f.DisposeAsync();

    Station Station => _f.App.Services.GetRequiredService<Station>();

    [Fact]
    public void A_number_no_job_has_is_JobNotFound_with_the_reason()
    {
        Assert.Equal(PlcCodes.JobNotFound, Station.LoadJob(99));
        Assert.Contains("No job has number 99", Station.LastError);
        Assert.Contains("Broken", Station.LastError);   // a file that cannot be read may hold it: say which
        Assert.Equal(PlcCodes.JobNotFound, Station.LoadJob(0));
        Assert.Contains("numbered from 1", Station.LastError);
    }

    [Fact]
    public void A_job_that_cannot_run_is_JobNotFound_naming_the_job_and_why_and_the_line_keeps_its_job()
    {
        Assert.Equal(0, Station.LoadJob(7));
        Assert.Equal("Labels", Station.Job?.Name);

        Assert.Equal(PlcCodes.JobNotFound, Station.LoadJob(12));
        Assert.Contains("Unteached", Station.LastError);
        Assert.Contains("no pattern taught", Station.LastError);
        Assert.Equal("Labels", Station.Job?.Name);

        Assert.Equal(PlcCodes.JobNotFound, Station.LoadJob(13));
        Assert.Contains("Broken", Station.LastError);
        Assert.Equal("Labels", Station.Job?.Name);

        Assert.Equal(0, Station.LoadJob(8));
        Assert.Equal("Empty", Station.Job?.Name);
        Assert.Null(Station.LastError);
    }
}
