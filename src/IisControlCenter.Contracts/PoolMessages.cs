namespace IisControlCenter.Contracts;

/// <summary>
/// The app pool settings the portal may read and change. This list is deliberately short and fixed.
/// In a change, a null field means "leave as it is". In a read, every field is filled.
/// There is no password and no custom account here, on purpose (see <see cref="PoolRules"/>).
/// </summary>
public sealed record PoolConfig
{
    /// <summary>"" = No Managed Code, otherwise "v2.0" or "v4.0".</summary>
    public string? ManagedRuntimeVersion { get; init; }
    /// <summary>Integrated or Classic.</summary>
    public string? PipelineMode { get; init; }
    public bool? Enable32Bit { get; init; }
    /// <summary>OnDemand or AlwaysRunning.</summary>
    public string? StartMode { get; init; }
    /// <summary>ApplicationPoolIdentity, NetworkService or LocalService. (Not LocalSystem, not a custom account.)</summary>
    public string? IdentityType { get; init; }
    public bool? LoadUserProfile { get; init; }
    /// <summary>Minutes of no requests before the worker process stops. 0 = never.</summary>
    public int? IdleTimeoutMinutes { get; init; }
    public int? QueueLength { get; init; }
    /// <summary>Recycle every N minutes. 0 = off.</summary>
    public int? RecycleIntervalMinutes { get; init; }
    /// <summary>Recycle at these local times, "HH:mm", at most 24.</summary>
    public IReadOnlyList<string>? RecycleTimes { get; init; }
    /// <summary>Recycle when private memory passes this many KB. 0 = off.</summary>
    public long? PrivateMemoryLimitKb { get; init; }
    public bool? RapidFailProtection { get; init; }
    public int? RapidFailMaxCrashes { get; init; }

    public bool IsEmpty => this == new PoolConfig();
}

public static class PoolActions
{
    public const string Start = "Start";
    public const string Stop = "Stop";
    public const string Recycle = "Recycle";
    public static readonly string[] All = [Start, Stop, Recycle];
}

public sealed record PoolActionCommand(string Pool, string Action);

/// <summary>
/// <paramref name="Expected"/> is what the person saw when they started editing, for the fields they change.
/// If the live value is different now, the agent refuses and nothing changes ("someone else changed it").
/// </summary>
public sealed record UpdatePoolCommand(string Pool, PoolConfig Change, PoolConfig Expected);

/// <summary>What the agent answers. <c>Before</c> and <c>After</c> are full snapshots, so the audit log shows exactly what changed.</summary>
public sealed record PoolOpResult(Guid CommandId, bool Ok, string? Error, string? StateBefore, string? StateAfter, PoolConfig? Before, PoolConfig? After, bool Changed);

/// <summary>Shared by the portal (to refuse early) and the agent (to refuse for real).</summary>
public static class PoolRules
{
    public static readonly string[] Runtimes = ["", "v2.0", "v4.0"];
    public static readonly string[] Pipelines = ["Integrated", "Classic"];
    public static readonly string[] StartModes = ["OnDemand", "AlwaysRunning"];
    public static readonly string[] Identities = ["ApplicationPoolIdentity", "NetworkService", "LocalService"];

    /// <summary>Pool names IIS allows are broader, but anything odd is refused rather than passed to IIS.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Choose an app pool.";
        if (name.Length > 64) return "The pool name is too long.";
        if (name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c))) return "The pool name has characters that are not allowed.";
        return null;
    }

    public static string? ValidateAction(string? action) => PoolActions.All.Contains(action ?? "", StringComparer.Ordinal) ? null : "Unknown action.";

    /// <summary>Returns the problems with a requested change (empty = fine).</summary>
    public static List<string> Validate(PoolConfig c)
    {
        var e = new List<string>();
        if (c.IsEmpty) { e.Add("Nothing to change."); return e; }
        if (c.ManagedRuntimeVersion is { } rt && !Runtimes.Contains(rt, StringComparer.Ordinal)) e.Add("The .NET CLR version must be: No Managed Code, v2.0 or v4.0.");
        if (c.PipelineMode is { } pm && !Pipelines.Contains(pm, StringComparer.Ordinal)) e.Add("The pipeline mode must be Integrated or Classic.");
        if (c.StartMode is { } sm && !StartModes.Contains(sm, StringComparer.Ordinal)) e.Add("The start mode must be OnDemand or AlwaysRunning.");
        if (c.IdentityType is { } id && !Identities.Contains(id, StringComparer.Ordinal))
            e.Add("The identity must be ApplicationPoolIdentity, NetworkService or LocalService. LocalSystem and custom accounts (which need a password) cannot be set from the portal: do that in IIS Manager on the server.");
        if (c.IdleTimeoutMinutes is { } it && (it < 0 || it > 1440)) e.Add("Idle time-out must be 0 (never) to 1440 minutes.");
        if (c.QueueLength is { } q && (q < 10 || q > 65535)) e.Add("Queue length must be between 10 and 65535.");
        if (c.RecycleIntervalMinutes is { } ri && (ri < 0 || ri > 525_600)) e.Add("The recycle interval must be 0 (off) to 525600 minutes.");
        if (c.RecycleTimes is { } times)
        {
            if (times.Count > 24) e.Add("At most 24 recycle times.");
            foreach (var t in times) if (!TimeSpan.TryParseExact(t, @"hh\:mm", null, out _)) e.Add($"'{t}' is not a time like 03:30.");
            if (times.Distinct().Count() != times.Count) e.Add("A recycle time is listed twice.");
        }
        if (c.PrivateMemoryLimitKb is { } m && (m < 0 || m > 1_073_741_824L)) e.Add("The private memory limit is out of range.");
        if (c.RapidFailMaxCrashes is { } rf && (rf < 1 || rf > 1000)) e.Add("Maximum crashes must be 1 to 1000.");
        return e;
    }

    /// <summary>Do the live values match what the person saw, for every field they want to change?</summary>
    public static List<string> Conflicts(PoolConfig live, PoolConfig expected, PoolConfig change)
    {
        var c = new List<string>();
        void Check<T>(string name, T? want, T? now, T? saw) where T : struct { if (want is not null && !EqualityComparer<T?>.Default.Equals(now, saw)) c.Add($"{name} is now {now}, not {saw}"); }
        void CheckS(string name, string? want, string? now, string? saw) { if (want is not null && !string.Equals(now ?? "", saw ?? "", StringComparison.Ordinal)) c.Add($"{name} is now '{now}', not '{saw}'"); }
        CheckS("CLR version", change.ManagedRuntimeVersion, live.ManagedRuntimeVersion, expected.ManagedRuntimeVersion);
        CheckS("Pipeline", change.PipelineMode, live.PipelineMode, expected.PipelineMode);
        Check("32-bit", change.Enable32Bit, live.Enable32Bit, expected.Enable32Bit);
        CheckS("Start mode", change.StartMode, live.StartMode, expected.StartMode);
        CheckS("Identity", change.IdentityType, live.IdentityType, expected.IdentityType);
        Check("Load user profile", change.LoadUserProfile, live.LoadUserProfile, expected.LoadUserProfile);
        Check("Idle time-out", change.IdleTimeoutMinutes, live.IdleTimeoutMinutes, expected.IdleTimeoutMinutes);
        Check("Queue length", change.QueueLength, live.QueueLength, expected.QueueLength);
        Check("Recycle interval", change.RecycleIntervalMinutes, live.RecycleIntervalMinutes, expected.RecycleIntervalMinutes);
        if (change.RecycleTimes is not null && !(live.RecycleTimes ?? []).SequenceEqual(expected.RecycleTimes ?? [])) c.Add("Recycle times were changed by someone else");
        Check("Private memory limit", change.PrivateMemoryLimitKb, live.PrivateMemoryLimitKb, expected.PrivateMemoryLimitKb);
        Check("Rapid-fail protection", change.RapidFailProtection, live.RapidFailProtection, expected.RapidFailProtection);
        Check("Rapid-fail max crashes", change.RapidFailMaxCrashes, live.RapidFailMaxCrashes, expected.RapidFailMaxCrashes);
        return c;
    }

    /// <summary>The fields that actually differ from the live values (a change that sets a value to what it already is does nothing).</summary>
    public static PoolConfig Effective(PoolConfig live, PoolConfig change) => new()
    {
        ManagedRuntimeVersion = change.ManagedRuntimeVersion is { } a && a != (live.ManagedRuntimeVersion ?? "") ? a : null,
        PipelineMode = change.PipelineMode is { } b && b != live.PipelineMode ? b : null,
        Enable32Bit = change.Enable32Bit is { } c && c != live.Enable32Bit ? c : null,
        StartMode = change.StartMode is { } d && d != live.StartMode ? d : null,
        IdentityType = change.IdentityType is { } e && e != live.IdentityType ? e : null,
        LoadUserProfile = change.LoadUserProfile is { } f && f != live.LoadUserProfile ? f : null,
        IdleTimeoutMinutes = change.IdleTimeoutMinutes is { } g && g != live.IdleTimeoutMinutes ? g : null,
        QueueLength = change.QueueLength is { } h && h != live.QueueLength ? h : null,
        RecycleIntervalMinutes = change.RecycleIntervalMinutes is { } i && i != live.RecycleIntervalMinutes ? i : null,
        RecycleTimes = change.RecycleTimes is { } j && !j.OrderBy(x => x).SequenceEqual((live.RecycleTimes ?? []).OrderBy(x => x)) ? j : null,
        PrivateMemoryLimitKb = change.PrivateMemoryLimitKb is { } k && k != live.PrivateMemoryLimitKb ? k : null,
        RapidFailProtection = change.RapidFailProtection is { } l && l != live.RapidFailProtection ? l : null,
        RapidFailMaxCrashes = change.RapidFailMaxCrashes is { } m && m != live.RapidFailMaxCrashes ? m : null,
    };
}
