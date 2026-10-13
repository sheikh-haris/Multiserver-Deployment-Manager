using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Logging;

namespace IisControlCenter.Agent.Pools;

/// <summary>
/// Start, stop, recycle and edit app pools. The agent checks everything itself, whatever the portal says:
/// the command envelope, its LOCAL Managed switch, the fixed list of editable settings and the live values.
/// </summary>
public sealed class PoolHandler(CommandPolicy policy, IPoolAdmin admin, ILogger<PoolHandler> log)
{
    // ServerManager commits fail if two changes overlap, so one pool operation at a time.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static PoolOpResult Fail(Guid id, string error, PoolSnapshot? now = null) =>
        new(id, false, error, now?.State, now?.State, now?.Config, now?.Config, false);

    private string? Refusal(CommandEnvelope e)
    {
        var r = policy.Validate(e);
        if (r is not null) return r;
        if (policy.EffectiveMode(e.Mode) != ServerMode.Managed)
            return "This server is in ReadOnly mode. App pools can only be changed on a server that is Managed in the portal AND was installed with Managed mode allowed.";
        return null;
    }

    public async Task<PoolOpResult> ActAsync(CommandEnvelope e, PoolActionCommand c)
    {
        if (Refusal(e) is { } why) return Fail(e.CommandId, why);
        if (PoolRules.ValidateName(c.Pool) is { } n) return Fail(e.CommandId, n);
        if (PoolRules.ValidateAction(c.Action) is { } a) return Fail(e.CommandId, a);
        await OneAtATime.WaitAsync();
        try
        {
            var before = admin.Get(c.Pool);
            if (before is null) return Fail(e.CommandId, $"App pool '{c.Pool}' does not exist on this server.");
            try
            {
                var state = await Task.Run(() => admin.Act(c.Pool, c.Action));
                var after = admin.Get(c.Pool);
                log.LogInformation("Pool {Pool}: {Action}. {Before} -> {After}", c.Pool, c.Action, before.State, state);
                return new PoolOpResult(e.CommandId, true, null, before.State, after?.State ?? state, before.Config, after?.Config, before.State != (after?.State ?? state));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Pool {Pool}: {Action} failed", c.Pool, c.Action);
                return Fail(e.CommandId, ex.Message, admin.Get(c.Pool));
            }
        }
        finally { OneAtATime.Release(); }
    }

    public async Task<PoolOpResult> UpdateAsync(CommandEnvelope e, UpdatePoolCommand c)
    {
        if (Refusal(e) is { } why) return Fail(e.CommandId, why);
        if (PoolRules.ValidateName(c.Pool) is { } n) return Fail(e.CommandId, n);
        var problems = PoolRules.Validate(c.Change);
        if (problems.Count > 0) return Fail(e.CommandId, string.Join(" ", problems));
        await OneAtATime.WaitAsync();
        try
        {
            var live = admin.Get(c.Pool);
            if (live is null) return Fail(e.CommandId, $"App pool '{c.Pool}' does not exist on this server.");
            var conflicts = PoolRules.Conflicts(live.Config, c.Expected, c.Change);
            if (conflicts.Count > 0)
                return Fail(e.CommandId, "The pool was changed by someone else since you opened it (" + string.Join("; ", conflicts) + "). Nothing was changed. Reload and try again.", live);

            var effective = PoolRules.Effective(live.Config, c.Change);
            if (effective.IsEmpty) return new PoolOpResult(e.CommandId, true, null, live.State, live.State, live.Config, live.Config, false);

            try
            {
                await Task.Run(() => admin.Apply(c.Pool, effective));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Pool {Pool}: update failed", c.Pool);
                return Fail(e.CommandId, "IIS refused the change: " + ex.Message, admin.Get(c.Pool));
            }
            var after = admin.Get(c.Pool);
            // Every changed field must now hold the wanted value: nothing may be left to change.
            var notApplied = after is null || PoolRules.Effective(after.Config, effective) is { IsEmpty: false };
            log.LogInformation("Pool {Pool}: updated {Changed}", c.Pool, effective);
            if (notApplied) return Fail(e.CommandId, "IIS accepted the change but the pool does not show the new value. Check it in IIS Manager.", after);
            return new PoolOpResult(e.CommandId, true, null, live.State, after!.State, live.Config, after.Config, true);
        }
        finally { OneAtATime.Release(); }
    }
}
