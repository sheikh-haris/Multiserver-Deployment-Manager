using System.Runtime.Versioning;
using IisControlCenter.Contracts;
using Microsoft.Web.Administration;

namespace IisControlCenter.Agent.Iis;

public sealed record PoolSnapshot(string State, PoolConfig Config);

/// <summary>Start, stop, recycle and edit one app pool. Only the settings in <see cref="PoolConfig"/> can be read or changed.</summary>
public interface IPoolAdmin
{
    /// <summary>Null when the pool does not exist.</summary>
    PoolSnapshot? Get(string pool);
    /// <summary>Returns the state after the action.</summary>
    string Act(string pool, string action);
    /// <summary>Applies only the non-null fields. The caller has already validated them.</summary>
    void Apply(string pool, PoolConfig change);
}

[SupportedOSPlatform("windows")]
public sealed class IisPoolAdmin : IPoolAdmin
{
    public static PoolConfig Read(ApplicationPool p) => new()
    {
        ManagedRuntimeVersion = p.ManagedRuntimeVersion ?? "",
        PipelineMode = p.ManagedPipelineMode.ToString(),
        Enable32Bit = p.Enable32BitAppOnWin64,
        StartMode = p.StartMode.ToString(),
        IdentityType = p.ProcessModel.IdentityType.ToString(),     // never the user name or password
        LoadUserProfile = p.ProcessModel.LoadUserProfile,
        IdleTimeoutMinutes = (int)p.ProcessModel.IdleTimeout.TotalMinutes,
        QueueLength = (int)p.QueueLength,
        RecycleIntervalMinutes = (int)p.Recycling.PeriodicRestart.Time.TotalMinutes,
        RecycleTimes = p.Recycling.PeriodicRestart.Schedule.Select(s => s.Time.ToString(@"hh\:mm")).OrderBy(x => x).ToList(),
        PrivateMemoryLimitKb = p.Recycling.PeriodicRestart.PrivateMemory,
        RapidFailProtection = p.Failure.RapidFailProtection,
        RapidFailMaxCrashes = (int)p.Failure.RapidFailProtectionMaxCrashes,
    };

    private static string State(ApplicationPool p) { try { return p.State.ToString(); } catch { return "Unknown"; } }

    public PoolSnapshot? Get(string pool)
    {
        using var mgr = new ServerManager();
        var p = mgr.ApplicationPools[pool];
        return p is null ? null : new PoolSnapshot(State(p), Read(p));
    }

    public string Act(string pool, string action)
    {
        using var mgr = new ServerManager();
        var p = mgr.ApplicationPools[pool] ?? throw new InvalidOperationException($"App pool '{pool}' not found.");
        switch (action)
        {
            case PoolActions.Start:
                if (p.State is ObjectState.Stopped or ObjectState.Stopping) p.Start();
                break;
            case PoolActions.Stop:
                if (p.State is ObjectState.Started or ObjectState.Starting) p.Stop();
                break;
            case PoolActions.Recycle:
                // A stopped pool cannot be recycled. Say so instead of silently starting it.
                if (p.State != ObjectState.Started) throw new InvalidOperationException($"App pool '{pool}' is {State(p)}; only a started pool can be recycled.");
                p.Recycle();
                break;
            default: throw new InvalidOperationException("Unknown action.");
        }
        // Start/Stop are asynchronous in IIS: wait briefly for the state to settle.
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(250);
            var s = State(mgr.ApplicationPools[pool]!);
            if (s is "Started" or "Stopped") return s;
        }
        return State(mgr.ApplicationPools[pool]!);
    }

    public void Apply(string pool, PoolConfig c)
    {
        using var mgr = new ServerManager();
        var p = mgr.ApplicationPools[pool] ?? throw new InvalidOperationException($"App pool '{pool}' not found.");
        if (c.ManagedRuntimeVersion is { } rt) p.ManagedRuntimeVersion = rt;
        if (c.PipelineMode is { } pm) p.ManagedPipelineMode = Enum.Parse<ManagedPipelineMode>(pm);
        if (c.Enable32Bit is { } b32) p.Enable32BitAppOnWin64 = b32;
        if (c.StartMode is { } sm) p.StartMode = Enum.Parse<StartMode>(sm);
        if (c.IdentityType is { } id) p.ProcessModel.IdentityType = Enum.Parse<ProcessModelIdentityType>(id);
        if (c.LoadUserProfile is { } lp) p.ProcessModel.LoadUserProfile = lp;
        if (c.IdleTimeoutMinutes is { } it) p.ProcessModel.IdleTimeout = TimeSpan.FromMinutes(it);
        if (c.QueueLength is { } q) p.QueueLength = q;
        if (c.RecycleIntervalMinutes is { } ri) p.Recycling.PeriodicRestart.Time = TimeSpan.FromMinutes(ri);
        if (c.RecycleTimes is { } times)
        {
            var sched = p.Recycling.PeriodicRestart.Schedule;
            sched.Clear();
            foreach (var t in times) sched.Add(TimeSpan.ParseExact(t, @"hh\:mm", null));
        }
        if (c.PrivateMemoryLimitKb is { } m) p.Recycling.PeriodicRestart.PrivateMemory = m;
        if (c.RapidFailProtection is { } rf) p.Failure.RapidFailProtection = rf;
        if (c.RapidFailMaxCrashes is { } mc) p.Failure.RapidFailProtectionMaxCrashes = mc;
        mgr.CommitChanges();
    }
}

/// <summary>Development helper: pools kept in memory, so the whole flow can be tried without IIS.</summary>
public sealed class SamplePoolAdmin : IPoolAdmin
{
    private readonly Dictionary<string, (string State, PoolConfig Config)> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public SamplePoolAdmin()
    {
        foreach (var n in new[] { "DefaultAppPool", "ShopPool", "ApiPool" })
            _pools[n] = ("Started", new PoolConfig
            {
                ManagedRuntimeVersion = "v4.0", PipelineMode = "Integrated", Enable32Bit = false, StartMode = "OnDemand", IdentityType = "ApplicationPoolIdentity",
                LoadUserProfile = false, IdleTimeoutMinutes = 20, QueueLength = 1000, RecycleIntervalMinutes = 1740, RecycleTimes = [], PrivateMemoryLimitKb = 0,
                RapidFailProtection = true, RapidFailMaxCrashes = 5,
            });
        // The discovery report has a "runs as LocalSystem" warning; the sample data needs one such pool to show it.
        _pools["PaymentsPool"] = ("Started", _pools["DefaultAppPool"].Config with { IdentityType = "LocalSystem", ManagedRuntimeVersion = "" });
    }

    public void Create(string name, PoolConfig? config)
    {
        lock (_gate)
        {
            if (_pools.ContainsKey(name)) throw new InvalidOperationException("The pool already exists.");
            _pools[name] = ("Started", _pools["DefaultAppPool"].Config);
        }
        if (config is { IsEmpty: false }) Apply(name, config);
    }

    public void Ensure(string name)
    {
        lock (_gate) if (!_pools.ContainsKey(name)) _pools[name] = _pools["DefaultAppPool"];
    }

    public IReadOnlyCollection<string> Names { get { lock (_gate) return _pools.Keys.ToList(); } }

    public PoolSnapshot? Get(string pool) { lock (_gate) return _pools.TryGetValue(pool, out var p) ? new PoolSnapshot(p.State, p.Config) : null; }

    public string Act(string pool, string action)
    {
        lock (_gate)
        {
            var p = _pools[pool];
            var state = action switch
            {
                PoolActions.Start => "Started",
                PoolActions.Stop => "Stopped",
                PoolActions.Recycle => p.State == "Started" ? "Started" : throw new InvalidOperationException($"App pool '{pool}' is {p.State}; only a started pool can be recycled."),
                _ => throw new InvalidOperationException("Unknown action."),
            };
            _pools[pool] = (state, p.Config);
            return state;
        }
    }

    public void Apply(string pool, PoolConfig c)
    {
        lock (_gate)
        {
            var (s, o) = _pools[pool];
            _pools[pool] = (s, o with
            {
                ManagedRuntimeVersion = c.ManagedRuntimeVersion ?? o.ManagedRuntimeVersion, PipelineMode = c.PipelineMode ?? o.PipelineMode, Enable32Bit = c.Enable32Bit ?? o.Enable32Bit,
                StartMode = c.StartMode ?? o.StartMode, IdentityType = c.IdentityType ?? o.IdentityType, LoadUserProfile = c.LoadUserProfile ?? o.LoadUserProfile,
                IdleTimeoutMinutes = c.IdleTimeoutMinutes ?? o.IdleTimeoutMinutes, QueueLength = c.QueueLength ?? o.QueueLength,
                RecycleIntervalMinutes = c.RecycleIntervalMinutes ?? o.RecycleIntervalMinutes, RecycleTimes = c.RecycleTimes ?? o.RecycleTimes,
                PrivateMemoryLimitKb = c.PrivateMemoryLimitKb ?? o.PrivateMemoryLimitKb, RapidFailProtection = c.RapidFailProtection ?? o.RapidFailProtection,
                RapidFailMaxCrashes = c.RapidFailMaxCrashes ?? o.RapidFailMaxCrashes,
            });
        }
    }
}
