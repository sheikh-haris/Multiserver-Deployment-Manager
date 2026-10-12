using System.Diagnostics;

namespace IisControlCenter.Contracts.Deployments;

public enum ScanOutcome { Clean, Infected, Error, Unavailable }

public sealed record ScanResult(ScanOutcome Outcome, string Detail);

/// <summary>
/// Runs Microsoft Defender's command line scanner on one file. Windows only.
/// "Unavailable" means the scanner could not be started (for example the account may not run it).
/// That is NOT treated as clean: the caller must scan somewhere else or refuse.
/// </summary>
public static class DefenderScanner
{
    public static string? FindMpCmdRun()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        try
        {
            if (Directory.Exists(platform))
            {
                var newest = Directory.GetDirectories(platform).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "MpCmdRun.exe")).FirstOrDefault(File.Exists);
                if (newest is not null) return newest;
            }
        }
        catch { /* fall through */ }
        var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(classic) ? classic : null;
    }

    public static async Task<ScanResult> ScanFileAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var exe = FindMpCmdRun();
        if (exe is null) return new(ScanOutcome.Unavailable, "Windows Defender command line scanner was not found.");
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(exe)
            {
                ArgumentList = { "-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation" },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new(ScanOutcome.Error, $"The scan did not finish within {timeout.TotalMinutes:0} minutes.");
            }
            var output = await outTask;
            // MpCmdRun: 0 = no threat found, 2 = threat found, anything else = the scan did not run properly.
            return p.ExitCode switch
            {
                0 => new(ScanOutcome.Clean, "No threats found."),
                2 => new(ScanOutcome.Infected, Tail(output)),
                5 => new(ScanOutcome.Unavailable, "Defender refused to run for this account (access denied)."),
                var c => new(ScanOutcome.Error, $"Scanner exit code {c}. {Tail(output)}"),
            };
        }
        catch (System.ComponentModel.Win32Exception ex) { return new(ScanOutcome.Unavailable, "Could not start the scanner: " + ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(ScanOutcome.Error, ex.GetType().Name + ": " + ex.Message); }
    }

    private static string Tail(string s) => s.Length <= 600 ? s.Trim() : "…" + s[^600..].Trim();
}
