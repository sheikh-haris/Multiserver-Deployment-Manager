using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Iis;

/// <summary>Read-only view of IIS. Implementations must never read passwords, connection strings or config file contents.</summary>
public interface IIisInspector
{
    bool IsIisInstalled();
    DiscoveryResult Collect(Guid commandId);
}
