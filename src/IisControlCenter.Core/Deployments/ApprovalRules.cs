namespace IisControlCenter.Core.Deployments;

public sealed record ApprovalCheck(bool Allowed, string? Reason, bool IsSelfApproval, bool Completes);

public static class ApprovalRules
{
    /// <summary>How many different approvers a request needs.</summary>
    public static int Required(bool isDirectToProd, int projectProdApprovers) => isDirectToProd ? Math.Clamp(projectProdApprovers, 1, 2) : 1;

    /// <summary>
    /// Can this person add an approval now? The requester cannot approve their own request unless the project allows
    /// self-approval for that environment (decision D5). Each approver counts once.
    /// </summary>
    public static ApprovalCheck Check(
        RequestStatus status, string requesterId, string approverId, bool approverHoldsApproverRole, bool selfApprovalAllowed,
        IReadOnlyCollection<string> existingApproverIds, int required)
    {
        if (status != RequestStatus.Submitted) return new(false, $"This request is {status}, not waiting for approval.", false, false);
        if (!approverHoldsApproverRole) return new(false, "You do not hold the Approver role for this project and environment.", false, false);
        if (existingApproverIds.Contains(approverId, StringComparer.OrdinalIgnoreCase)) return new(false, "You have already approved this request.", false, false);
        var self = string.Equals(requesterId, approverId, StringComparison.OrdinalIgnoreCase);
        if (self && !selfApprovalAllowed) return new(false, "You cannot approve your own request.", true, false);
        var completes = existingApproverIds.Count + 1 >= required;
        return new(true, null, self, completes);
    }

    public static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(7);
}
