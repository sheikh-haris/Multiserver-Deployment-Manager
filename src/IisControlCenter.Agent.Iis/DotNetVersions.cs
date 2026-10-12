using IisControlCenter.Contracts;
using Microsoft.Win32;

namespace IisControlCenter.Agent.Iis;

public static class DotNetVersions
{
    public static IReadOnlyList<DiscoveredDotNetVersion> Read()
    {
        var list = new List<DiscoveredDotNetVersion>();
        if (!OperatingSystem.IsWindows()) return list;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            if (key?.GetValue("Release") is int release)
                list.Add(new DiscoveredDotNetVersion(".NET Framework", FrameworkName(release)));
        }
        catch { }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var (kind, folder) in new[]
        {
            (".NET runtime", @"dotnet\shared\Microsoft.NETCore.App"),
            ("ASP.NET Core runtime", @"dotnet\shared\Microsoft.AspNetCore.App"),
        })
        {
            try
            {
                var dir = Path.Combine(pf, folder);
                if (Directory.Exists(dir))
                    foreach (var d in Directory.GetDirectories(dir)) list.Add(new DiscoveredDotNetVersion(kind, Path.GetFileName(d)));
            }
            catch { }
        }

        try
        {
            var module = Path.Combine(pf, @"IIS\Asp.Net Core Module\V2\aspnetcorev2.dll");
            if (File.Exists(module))
                list.Add(new DiscoveredDotNetVersion("ASP.NET Core Module V2", System.Diagnostics.FileVersionInfo.GetVersionInfo(module).FileVersion ?? "installed"));
        }
        catch { }
        return list;
    }

    public static string FrameworkName(int release) => release switch
    {
        >= 533320 => "4.8.1",
        >= 528040 => "4.8",
        >= 461808 => "4.7.2",
        >= 461308 => "4.7.1",
        >= 460798 => "4.7",
        >= 394802 => "4.6.2",
        >= 394254 => "4.6.1",
        >= 393295 => "4.6",
        >= 379893 => "4.5.2",
        >= 378675 => "4.5.1",
        >= 378389 => "4.5",
        _ => $"4.x (release {release})",
    };
}
