using System.IO.Compression;
using System.Security.Cryptography;
using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Core.Deployments;

public sealed record ZipPolicy(
    int MaxEntries = 50_000,
    long MaxUncompressedBytes = 5L * 1024 * 1024 * 1024,
    /// <summary>Per-entry limit for large entries: uncompressed size divided by compressed size.</summary>
    double MaxCompressionRatio = 1000,
    long RatioCheckMinBytes = 10L * 1024 * 1024,
    IReadOnlySet<string>? AllowedExtraExtensions = null)
{
    /// <summary>Hygiene, not security: .dll and .aspx are the real payload. The approver and the antivirus scan are the controls.</summary>
    public static readonly string[] BlockedExtensions = [".exe", ".bat", ".cmd", ".ps1", ".msi"];
}

public sealed record ZipIssue(string Path, string Message);

public sealed record ZipValidation(
    IReadOnlyList<ZipIssue> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PackageFile> Files,
    long TotalUncompressedBytes)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ZipValidator
{
    /// <summary>
    /// Checks a package and returns its file list with SHA-256 per file (the manifest used for the diff).
    /// Rejects zip-slip paths, absolute paths, streams, reserved names, duplicates (case-insensitive),
    /// symbolic links, zip bombs, too many entries and blocked file types.
    /// </summary>
    public static ZipValidation Validate(Stream zip, ZipPolicy policy, CancellationToken ct = default)
    {
        var errors = new List<ZipIssue>();
        var warnings = new List<string>();
        var files = new List<PackageFile>();
        long total = 0;

        ZipArchive archive;
        try { archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            return new ZipValidation([new ZipIssue("", "The file is not a valid zip archive.")], [], [], 0);
        }

        using (archive)
        {
            IReadOnlyList<ZipArchiveEntry> entries;
            try { entries = archive.Entries.ToList(); }
            catch (InvalidDataException) { return new ZipValidation([new ZipIssue("", "The zip file is damaged.")], [], [], 0); }

            if (entries.Count > policy.MaxEntries)
                return new ZipValidation([new ZipIssue("", $"The zip has {entries.Count} entries. The limit is {policy.MaxEntries}.")], [], [], 0);

            var extra = policy.AllowedExtraExtensions ?? new HashSet<string>();
            var seen = new HashSet<string>(RelativePaths.Comparer);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var path = RelativePaths.Normalize(entry.FullName, out var pathError);

                if (path is null)
                {
                    // A directory entry like "./" is harmless; anything else with a bad name is refused.
                    if (isDirectory && pathError is "empty path") continue;
                    errors.Add(new ZipIssue(entry.FullName, $"Unsafe or invalid path ({pathError})."));
                    continue;
                }
                if (isDirectory) continue;

                if (IsSymlink(entry)) { errors.Add(new ZipIssue(path, "Symbolic links are not allowed.")); continue; }
                if (!seen.Add(path)) { errors.Add(new ZipIssue(path, "Duplicate file name (names are compared without regard to upper/lower case).")); continue; }

                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ZipPolicy.BlockedExtensions.Contains(ext) && !extra.Contains(ext))
                    errors.Add(new ZipIssue(path, $"File type {ext} is blocked for this component."));

                if (entry.Length > policy.RatioCheckMinBytes && entry.CompressedLength > 0 &&
                    (double)entry.Length / entry.CompressedLength > policy.MaxCompressionRatio)
                {
                    errors.Add(new ZipIssue(path, "Suspicious compression ratio (possible zip bomb)."));
                    continue;
                }

                // Read for real: the declared size in the header can be wrong, so the cap is enforced on actual bytes.
                try
                {
                    using var s = entry.Open();
                    using var sha = SHA256.Create();
                    var buffer = new byte[81920];
                    long read = 0; int n;
                    while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        read += n; total += n;
                        if (total > policy.MaxUncompressedBytes)
                            return new ZipValidation([.. errors, new ZipIssue("", $"The package is larger than {policy.MaxUncompressedBytes / (1024 * 1024)} MB when unpacked (possible zip bomb).")], warnings, files, total);
                        if (read > entry.Length) { errors.Add(new ZipIssue(path, "File is larger than its declared size.")); break; }
                        sha.TransformBlock(buffer, 0, n, null, 0);
                        ct.ThrowIfCancellationRequested();
                    }
                    sha.TransformFinalBlock([], 0, 0);
                    files.Add(new PackageFile(path, read, Convert.ToHexString(sha.Hash!)));
                }
                catch (InvalidDataException) { errors.Add(new ZipIssue(path, "The file is damaged or encrypted.")); }
                catch (NotSupportedException) { errors.Add(new ZipIssue(path, "Unsupported compression or encryption.")); }
            }
        }

        if (errors.Count == 0 && files.Count == 0) errors.Add(new ZipIssue("", "The zip contains no files."));

        if (errors.Count == 0 && files.Count > 1)
        {
            var tops = files.Select(f => f.Path.Contains('/') ? f.Path[..f.Path.IndexOf('/')] : null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tops.Count == 1 && tops[0] is not null)
                warnings.Add($"Every file is inside one top-level folder '{tops[0]}/'. It will be deployed under that folder, not into the site root. Check this is what you want.");
        }
        return new ZipValidation(errors, warnings, files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), total);
    }

    private static bool IsSymlink(ZipArchiveEntry e)
    {
        // Unix mode in the high 16 bits of the external attributes; 0xA000 = symbolic link.
        var unixMode = (e.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }
}
