using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Sundaram.RemoteService;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using RemoteRequest = VisionStudio.Remote.V1.RemoteRequest;

namespace VisionSystem.Runtime;

/// <summary>
/// What remote service asks at the line PC (REMOTE-SUPPORT.md 4b): the operator's confirmation of a change (a job file put in
/// place, an action) and a supervisor's approval of a correction. Requests go out in Status.remote_service.requests; Vision
/// Studio shows them and answers with AnswerRemoteRequest. A request nobody answers in time is refused.
/// <para>Shielding (REQ-08): a request says what changes, never who asks: "Sundaram remote service".</para>
/// </summary>
public sealed class RemoteRequests(Recorder recorder, TimeProvider? time = null) : IOperatorConfirmation, ISupervisorApproval
{
    static readonly TimeSpan ConfirmFor = TimeSpan.FromMinutes(10), ApproveFor = TimeSpan.FromMinutes(30);
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, (RemoteRequest Request, TaskCompletionSource<string?> Answer, DateTimeOffset Asked)> _open = new();

    /// <summary>Raised when a request comes or goes (the line's status is published again).</summary>
    public event Action? Changed;

    /// <summary>The open requests, oldest first.</summary>
    public IReadOnlyList<RemoteRequest> Open => _open.Values.OrderBy(v => v.Asked).Select(v => v.Request).ToList();

    public async Task<bool> ConfirmAsync(ConfirmRequest r, CancellationToken ct) =>
        await Ask(new RemoteRequest { Kind = RemoteRequest.Types.Kind.Confirm, Title = r.Title, What = r.What }, ConfirmFor, ct) is not null;

    public Task<string?> ApproveAsync(Correction c, CancellationToken ct)
    {
        var request = new RemoteRequest
        {
            Kind = RemoteRequest.Types.Kind.Approve, Title = "Remote service: correct recorded values",
            What = $"{c.Kind} {c.Record}", Reason = c.Reason,
        };
        request.Changes.AddRange(c.Changes.Select(ch => new RemoteChange { Field = ch.Key, OldValue = ch.Value.Old, NewValue = ch.Value.New }));
        return Ask(request, ApproveFor, ct);
    }

    async Task<string?> Ask(RemoteRequest request, TimeSpan wait, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        request.Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        request.Expires = Timestamp.FromDateTimeOffset(now + wait);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _open[request.Id] = (request, answer, now);
        Changed?.Invoke();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        using var reg = timeout.Token.Register(() => answer.TrySetResult(null));
        try { return await answer.Task; }
        finally
        {
            _open.TryRemove(request.Id, out _);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The answer at the line PC. Only on the local endpoint. A No needs nobody's sign-in. A Yes to a confirmation is the
    /// person at the line PC (the signed-in Vision Studio user, else "the operator at the line PC"). A Yes to an approval needs
    /// a supervisor's own PIN, password or card, checked like a changeover's second person (wrong tries counted the same way),
    /// with the role Supervisor, QA or Admin.
    /// </summary>
    public AnswerRemoteRequestResponse Answer(AnswerRemoteRequestRequest req, ServerCallContext ctx, Sessions sessions)
    {
        static AnswerRemoteRequestResponse No(uint code, string text, string key) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (!Sessions.IsLocal(ctx)) return No(403, "Answer remote service at the line PC.", "runtime.remote.localOnly");
        Caller? caller;
        try { caller = sessions.Read(ctx); }
        catch (RpcException) { caller = null; }   // a Studio token from before a restart: still the person at the line PC
        if (!_open.TryGetValue(req.Id ?? "", out var open)) return No(404, "This request is no longer open.", "runtime.remote.gone");
        var request = open.Request;

        if (!req.Approve)
        {
            if (Take(request.Id, open.Answer, null)) Note($"Remote service request refused at the line PC: {request.Title}: {request.What}");
            return new AnswerRemoteRequestResponse { Ok = true };
        }
        if (request.Kind == RemoteRequest.Types.Kind.Confirm)
        {
            var who = caller?.User?.FullName is { Length: > 0 } n ? n : "the operator at the line PC";
            if (!Take(request.Id, open.Answer, who)) return No(404, "This request is no longer open.", "runtime.remote.gone");
            Note($"Remote service request confirmed by {who}: {request.Title}: {request.What}");
            return new AnswerRemoteRequestResponse { Ok = true };
        }

        var person = new AcceptChangeoverRequest { User = req.User ?? "" };
        switch (req.SecretCase)
        {
            case AnswerRemoteRequestRequest.SecretOneofCase.Password: person.Password = req.Password; break;
            case AnswerRemoteRequestRequest.SecretOneofCase.Pin: person.Pin = req.Pin; break;
            case AnswerRemoteRequestRequest.SecretOneofCase.Card: person.Card = req.Card; break;
            default: return No(400, "A supervisor approves with their PIN, password or card.", "runtime.remote.secret");
        }
        var (user, refused) = sessions.SecondPerson(person, ctx);
        if (refused is not null) return No(refused.ErrorCode, refused.ErrorText, refused.ErrorKey);
        if (!user!.Roles.Any(r => r is Role.Supervisor or Role.Qa or Role.Admin))
            return No(403, "Only a supervisor, QA or an administrator approves a correction.", "runtime.remote.notSupervisor");
        var approver = user.FullName is { Length: > 0 } f ? f : user.Name;
        if (!Take(request.Id, open.Answer, approver)) return No(404, "This request is no longer open.", "runtime.remote.gone");
        Note($"Remote service correction approved by {approver}: {request.What}");
        return new AnswerRemoteRequestResponse { Ok = true };
    }

    /// <summary>The first answer wins: the request leaves the list at once (the next status no longer shows it), and a second
    /// answer (a double tap, another screen) finds it gone instead of recording a second outcome.</summary>
    bool Take(string id, TaskCompletionSource<string?> answer, string? result)
    {
        if (!_open.TryRemove(id, out _)) return false;
        answer.TrySetResult(result);
        Changed?.Invoke();
        return true;
    }

    void Note(string detail)
    {
        try { recorder.Note("remote_support", detail); } catch (Exception) { }
    }
}

/// <summary>The line may be changed only while it is stopped (line safety, requirements 12).</summary>
public sealed class LineStopped(Station station) : IMachineState
{
    public bool SafeToChange(out string reason)
    {
        if (station.Running)
        {
            reason = "the line is running; stop it first";
            return false;
        }
        reason = "";
        return true;
    }
}
