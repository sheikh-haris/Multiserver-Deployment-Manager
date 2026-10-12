namespace IisControlCenter.Core.Deployments;

public enum RequestStatus
{
    Draft, Submitted, Approved, Scheduled, Running, Succeeded, Failed, RolledBack, Rejected, Expired, Cancelled, NeedsReapproval,
}

public enum RequestType { Deploy, Rollback }

public enum RunStatus { Pending, Running, Succeeded, Failed, RolledBack, Skipped, NeedsReapproval }

/// <summary>The only place that decides which status changes are legal.</summary>
public static class RequestStateMachine
{
    private static readonly Dictionary<RequestStatus, RequestStatus[]> Allowed = new()
    {
        [RequestStatus.Draft] = [RequestStatus.Submitted, RequestStatus.Cancelled],
        [RequestStatus.Submitted] = [RequestStatus.Approved, RequestStatus.Scheduled, RequestStatus.Rejected, RequestStatus.Expired, RequestStatus.Cancelled],
        [RequestStatus.Approved] = [RequestStatus.Running, RequestStatus.Scheduled, RequestStatus.Cancelled, RequestStatus.NeedsReapproval, RequestStatus.Failed, RequestStatus.Expired],
        [RequestStatus.Scheduled] = [RequestStatus.Running, RequestStatus.Cancelled, RequestStatus.NeedsReapproval, RequestStatus.Failed, RequestStatus.Expired],
        [RequestStatus.Running] = [RequestStatus.Succeeded, RequestStatus.Failed, RequestStatus.RolledBack, RequestStatus.NeedsReapproval],
        [RequestStatus.NeedsReapproval] = [RequestStatus.Submitted, RequestStatus.Cancelled, RequestStatus.Expired],
    };

    public static bool IsTerminal(RequestStatus s) => !Allowed.ContainsKey(s);

    public static bool CanTransition(RequestStatus from, RequestStatus to) => Allowed.TryGetValue(from, out var l) && l.Contains(to);

    public static void EnsureCanTransition(RequestStatus from, RequestStatus to)
    {
        if (!CanTransition(from, to)) throw new InvalidOperationException($"A request cannot change from {from} to {to}.");
    }

    /// <summary>The requester may cancel until execution starts.</summary>
    public static bool CanCancel(RequestStatus s) => CanTransition(s, RequestStatus.Cancelled);

    public static bool IsOpen(RequestStatus s) => s is RequestStatus.Submitted or RequestStatus.Approved or RequestStatus.Scheduled or RequestStatus.Running or RequestStatus.NeedsReapproval;
}
