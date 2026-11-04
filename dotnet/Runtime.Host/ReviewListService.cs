using Grpc.Core;
using VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

/// <summary>
/// The review follow-ups (requirements AS-04i..k): the supervisor's list of reviewed pictures, removing a decision, and
/// the operator's "Was OK" flag. A service of its own beside <see cref="RuntimeService"/> on the same channel and
/// sign-in; changing a decision is RuntimeService.ReviewPart with the reviewed picture's id.
/// </summary>
public sealed class NgReviewsService(Sessions sessions, ReviewDesk reviews) : NgReviews.NgReviewsBase
{
    // as the other review and sorting calls: Supervisor, QA, Engineer, Admin, and Support or OEM opened with today's code (AS-04h)
    public override Task<ListReviewedResponse> ListReviewed(ListReviewedRequest request, ServerCallContext context)
    {
        sessions.Learn(context);
        return Task.Run(() => reviews.Reviewed(request), context.CancellationToken);
    }

    public override Task<RemoveReviewResponse> RemoveReview(RemoveReviewRequest request, ServerCallContext context)
    {
        var c = sessions.Learn(context);
        var r = reviews.Remove(request, c.User?.Name ?? "");
        sessions.AuditSupport(c, $"Decision on picture {request.ImageId} removed: " + (r.Ok ? "done" : "refused, " + r.ErrorText));
        return Task.FromResult(r);
    }

    /// <summary>Anyone signed in to Vision Studio who may run the line, operators too: the flag only tells the supervisor.</summary>
    public override Task<FlagWasOkResponse> FlagWasOk(FlagWasOkRequest request, ServerCallContext context)
    {
        var c = sessions.Operate(context);
        // a person signed in to Vision Studio, so the supervisor knows who said it (AS-04k); never plant software
        if (c.Client != ClientKind.Studio || c.User is null)
            throw new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, "Sign in to Vision Studio to mark a part."));
        return Task.FromResult(reviews.FlagWasOk(request, c.User.Name));
    }
}
