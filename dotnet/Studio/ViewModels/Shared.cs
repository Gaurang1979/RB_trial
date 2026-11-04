using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>Runs an action on the UI thread (the app) or at once (tests).</summary>
public interface IUi
{
    void Post(Action action);
}

public sealed class ImmediateUi : IUi
{
    public void Post(Action action) => action();
}

/// <summary>Status colour of an element; the view maps it to Vs.Brush.Status.* tokens.</summary>
public enum Tone { Neutral, Ok, Ng, Warn, Duplicate, Stopped, Info }

/// <summary>How a tool's result is marked. A report-only tool passed without deciding the part: information,
/// never OK. A failed tool is NG whatever else it says (line safety).</summary>
public static class ToolMark
{
    public static Tone Tone(bool pass, bool reportOnly) => !pass ? ViewModels.Tone.Ng : reportOnly ? ViewModels.Tone.Info : ViewModels.Tone.Ok;
    public static string Icon(bool pass, bool reportOnly) => !pass ? Icons.Cross : reportOnly ? Icons.Info : Icons.Check;
    public static string Mark(bool pass, bool reportOnly) => !pass ? "✗" : reportOnly ? "ⓘ" : "✓";
    /// <summary>The string-table key of the word: NG, Info or OK.</summary>
    public static string Word(bool pass, bool reportOnly) => !pass ? "result.ng" : reportOnly ? "result.info" : "result.ok";
}

/// <summary>Shape that goes with a tone (UI-UX-SPEC.md principle 5); names Icon.* resources.</summary>
public static class Icons
{
    public const string Check = "Icon.Check";
    public const string Cross = "Icon.Cross";
    public const string Bang = "Icon.Bang";
    public const string Pause = "Icon.Pause";
    public const string Info = "Icon.Info";
    public const string Play = "Icon.Play";
    public const string Stop = "Icon.Stop";
    public const string Lock = "Icon.Lock";
    public const string Plug = "Icon.Plug";
    public const string Camera = "Icon.Camera";
    public const string Duplicate = "Icon.Duplicate";
    public const string Product = "Icon.Product";
    public const string Help = "Icon.Help";
    public const string List = "Icon.List";
    public const string User = "Icon.User";
    public const string Language = "Icon.Language";
    public const string Back = "Icon.Back";
    public const string Previous = "Icon.Previous";
    public const string Next = "Icon.Next";
    public const string Delete = "Icon.Delete";
    public const string Wait = "Icon.Wait";
    public const string Batch = "Icon.Batch";
    public const string Code = "Icon.Code";
    public const string Text = "Icon.Text";
    public const string Blob = "Icon.Blob";
    public const string Save = "Icon.Save";
    public const string Undo = "Icon.Undo";
    public const string Redo = "Icon.Redo";
    public const string Teach = "Icon.Teach";
    public const string TryAll = "Icon.TryAll";
    public const string Fit = "Icon.Fit";
    public const string Pan = "Icon.Pan";

    public static string For(Verdict verdict) => verdict switch
    {
        Verdict.Ok => Check,
        Verdict.Ng => Cross,
        Verdict.Warn => Bang,
        Verdict.Duplicate => Duplicate,
        Verdict.Info => Info,
        _ => Info,
    };
}

public static class Tones
{
    public static Tone For(Verdict verdict) => verdict switch
    {
        Verdict.Ok => Tone.Ok,
        Verdict.Ng => Tone.Ng,
        Verdict.Warn => Tone.Warn,
        Verdict.Duplicate => Tone.Duplicate,
        Verdict.Info => Tone.Info,
        _ => Tone.Neutral,
    };

    /// <summary>The shape that goes with a tone (principle 5).</summary>
    public static string IconFor(Tone tone) => tone switch
    {
        Tone.Ok => Icons.Check,
        Tone.Ng => Icons.Cross,
        Tone.Warn => Icons.Bang,
        Tone.Duplicate => Icons.Duplicate,
        Tone.Stopped => Icons.Pause,
        _ => Icons.Info,
    };

    /// <summary>Worse verdicts rank higher: NG over duplicate over warning over OK.</summary>
    public static int Rank(Verdict verdict) => verdict switch
    {
        Verdict.Ng => 4,
        Verdict.Duplicate => 3,
        Verdict.Warn => 2,
        Verdict.Ok => 1,
        _ => 0,
    };
}

public enum AppTheme { Dark, Light, HighContrast }

/// <summary>
/// The machine state shown in the status pill (UI-UX-SPEC.md 2.1). The highest active state
/// wins: not connected, licence locked, PLC lost, no camera, alarm, warning, running, stopped.
/// </summary>
public sealed record PillState(Tone Tone, string Icon, string TextKey)
{
    public static PillState From(LinkState link, RuntimeStatus? status)
    {
        if (link == LinkState.Connecting) return new(Tone.Stopped, Icons.Wait, "status.connecting");
        if (link != LinkState.Connected || status is null) return new(Tone.Ng, Icons.Cross, "status.not_connected");
        if (!status.LicenceOk) return new(Tone.Stopped, Icons.Lock, "status.locked");
        if (!status.PlcOk) return new(Tone.Ng, Icons.Plug, "status.plc_lost");
        if (!status.CameraOk) return new(Tone.Ng, Icons.Camera, "status.no_camera");
        if (status.Alarm is not null) return new(Tone.Ng, Icons.Bang, "status.alarm");
        if (status.Warning is not null) return new(Tone.Warn, Icons.Bang, "status.check");
        return status.State == LineState.Running
            ? new(Tone.Ok, Icons.Play, "status.running")
            : new(Tone.Stopped, Icons.Pause, "status.stopped");
    }
}

/// <summary>Calls the runtime on the current connection; a failure drops the link to reconnect.</summary>
public sealed class RuntimeCalls(IRuntimeLink link)
{
    public IRuntimeLink Link => link;

    /// <summary>A job designer or Start batch call; not reached when the runtime has no such calls.</summary>
    public Task<(bool Reached, T? Value)> TryDesignAsync<T>(Func<IJobDesignClient, CancellationToken, Task<T>> call, TimeSpan? timeout = null) =>
        TryAsync((c, ct) => c is IJobDesignClient d ? call(d, ct) : throw new NotSupportedException("no job designer calls"), timeout);

    /// <summary>An NG review or learning call; not reached when the runtime has no such calls. <paramref name="cancel"/>
    /// stops it (Stop learning): answered, without a value, and the link stays up.</summary>
    public Task<(bool Reached, T? Value)> TryReviewAsync<T>(Func<IReviewClient, CancellationToken, Task<T>> call, TimeSpan? timeout = null,
        CancellationToken cancel = default) =>
        TryAsync((c, ct) => c is IReviewClient r ? call(r, ct) : throw new NotSupportedException("no NG review calls"), timeout, cancel);

    public async Task<(bool Reached, T? Value)> TryAsync<T>(Func<IRuntimeClient, CancellationToken, Task<T>> call, TimeSpan? limit = null,
        CancellationToken cancel = default)
    {
        var client = link.Client;
        if (client is null) return (false, default);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(10));
            return (true, await call(client, timeout.Token));
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            return (true, default);   // the person stopped it: the runtime is fine
        }
        catch (Grpc.Core.RpcException e) when (!GrpcRuntimeClient.IsTransport(e.StatusCode))
        {
            // The runtime answered, only not with what was asked (a refusal, no answer in time):
            // the link is fine, the caller shows its own "not possible" text.
            return (true, default);
        }
        catch (NotSupportedException)
        {
            return (true, default);   // an older runtime: answered, only without this call
        }
        catch (Exception)
        {
            link.ReportBroken(client);
            return (false, default);
        }
    }
}
