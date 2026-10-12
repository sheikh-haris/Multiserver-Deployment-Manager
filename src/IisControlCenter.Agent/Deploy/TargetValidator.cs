using IisControlCenter.Agent.Iis;
using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Deploy;

/// <summary>
/// The agent's own checks on a folder the portal names. Three things must all be true:
/// the folder is inside this server's local allow-list, IIS really has that site and application,
/// and IIS uses exactly that folder for it. So a portal can never aim the agent at some other folder.
/// </summary>
public sealed class TargetValidator(ExecutorOptions options, ISiteResolver sites)
{
    public ResolvedApp Validate(string physicalPath, long siteId, string appPath)
    {
        if (!options.IsPathAllowed(physicalPath))
            throw new StepFailedException(Steps.Validate, "This folder is outside the folders this server's agent is allowed to change (set when the agent was installed).");
        var app = sites.Resolve(siteId, appPath) ?? throw new StepFailedException(Steps.Validate, $"IIS site {siteId} application '{appPath}' was not found on this server.");
        if (!string.Equals(Path.GetFullPath(app.PhysicalPath).TrimEnd('\\', '/'), Path.GetFullPath(physicalPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new StepFailedException(Steps.Validate, $"The folder in the request ('{physicalPath}') is not the folder IIS uses for this application ('{app.PhysicalPath}'). The site was changed after the request was made.", RunResults.NeedsReapproval);
        if (!Directory.Exists(physicalPath)) throw new StepFailedException(Steps.Validate, "The target folder does not exist.");
        if (FolderScanner.IsReparsePoint(physicalPath)) throw new StepFailedException(Steps.Validate, "The target folder is a link. The agent does not deploy through links.");
        return app;
    }
}
