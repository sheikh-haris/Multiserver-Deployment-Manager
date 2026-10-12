namespace IisControlCenter.Agent;

public static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
