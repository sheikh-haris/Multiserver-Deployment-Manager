using System.Security.Cryptography;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Tests;

public class UploadServiceTests : IDisposable
{
    private readonly DeployEnv d = new();
    public void Dispose() => d.Dispose();

    private static byte[] Big(int mb)
    {
        var data = new byte[mb * 1024 * 1024 + 123];
        RandomNumberGenerator.Fill(data);
        return DeployEnv.Zip(("big.bin", Convert.ToBase64String(data)), ("index.html", "x"));      // base64 so it is mostly incompressible text
    }

    [Fact]
    public async Task Only_a_deployer_of_that_environment_can_start_an_upload_and_only_zip_files()
    {
        await d.SetUp();
        Assert.Equal("Only .zip files can be uploaded.", (await d.Uploads.StartAsync(d.Deployer("Dev"), d.ComponentId, "Dev", "x.exe", 10)).Error);
        Assert.False((await d.Uploads.StartAsync(d.Deployer("Dev"), d.ComponentId, "Dev", "x.zip", 0)).Ok);
        Assert.False((await d.Uploads.StartAsync(d.Deployer("Dev"), d.ComponentId, "Dev", "x.zip", 100L * 1024 * 1024)).Ok);        // over the 20 MB limit of the test
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Uploads.StartAsync(d.Person("v", (Role.ProjectViewer, "Dev")), d.ComponentId, "Dev", "x.zip", 10));
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Uploads.StartAsync(d.Person("w", (Role.Deployer, "Staging")), d.ComponentId, "Dev", "x.zip", 10));
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Uploads.StartAsync(d.E.Admin, d.ComponentId, "Dev", "x.zip", 10));
    }

    [Fact]
    public async Task A_multi_chunk_upload_can_be_resumed_and_ends_with_the_right_hash()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var zip = Big(2);
        var s = (await d.Uploads.StartAsync(dep, d.ComponentId, "Dev", "big.zip", zip.Length)).Value!;
        Assert.True(s.TotalChunks >= 2);
        var chunk = (int)s.ChunkSize;
        byte[] Part(int i) => zip.Skip(i * chunk).Take(chunk).ToArray();

        Assert.True((await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(Part(0)), null, default)).Ok);
        // "connection lost": ask what was received, then send the rest
        var info = await d.Uploads.GetAsync(dep, s.SessionId);
        Assert.Equal([0], info!.Received);
        Assert.False((await d.Uploads.CompleteAsync(dep, s.SessionId)).Ok);                  // chunks missing
        for (var i = 1; i < s.TotalChunks; i++) Assert.True((await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, i, new MemoryStream(Part(i)), null, default)).Ok);
        Assert.True((await d.Uploads.CompleteAsync(dep, s.SessionId)).Ok);
        await d.Uploads.ProcessPendingAsync();

        info = await d.Uploads.GetAsync(dep, s.SessionId);
        Assert.Equal(UploadStatus.Ready, info!.Status);
        await using var db = d.E.Db.CreateDbContext();
        var pkg = await db.Packages.SingleAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(zip)), pkg.Sha256);
        Assert.Equal(zip.Length, pkg.Size);
        Assert.Equal(AvResult.Clean, pkg.AvResult);
        Assert.Equal(2, d.Store.ReadManifest(pkg).Count);
        Assert.Empty(Directory.GetFiles(d.Store.UploadTempRoot, "*", SearchOption.AllDirectories));      // working files removed
        Assert.Contains("Package.Upload:Success", await d.AuditActions());
    }

    [Fact]
    public async Task Damaged_wrong_sized_and_out_of_range_chunks_are_refused()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var zip = DeployEnv.Zip(("a.txt", "hello"));
        var s = (await d.Uploads.StartAsync(dep, d.ComponentId, "Dev", "a.zip", zip.Length)).Value!;
        var bad = await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(zip), new string('0', 64), default);
        Assert.False(bad.Ok); Assert.Contains("damaged", bad.Error);
        Assert.False((await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(zip.Take(10).ToArray()), null, default)).Ok);
        Assert.False((await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(zip.Concat(new byte[5]).ToArray()), null, default)).Ok);
        Assert.False((await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 7, new MemoryStream(zip), null, default)).Ok);
        Assert.Empty((await d.Uploads.GetAsync(dep, s.SessionId))!.Received);
    }

    [Fact]
    public async Task Another_user_cannot_see_or_feed_my_upload()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev"); var other = d.Deployer("Dev", "dep2");
        var zip = DeployEnv.Zip(("a.txt", "hello"));
        var s = (await d.Uploads.StartAsync(dep, d.ComponentId, "Dev", "a.zip", zip.Length)).Value!;
        Assert.Null(await d.Uploads.GetAsync(other, s.SessionId));
        Assert.False((await d.Uploads.ReceiveChunkAsync(other, s.SessionId, 0, new MemoryStream(zip), null, default)).Ok);
        Assert.False((await d.Uploads.CompleteAsync(other, s.SessionId)).Ok);
    }

    [Fact]
    public async Task Dangerous_zip_is_rejected_with_the_reasons_and_the_rejection_is_audited()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var zip = DeployEnv.Zip(("../evil.dll", "x"), ("tools/run.exe", "MZ"), ("ok.txt", "fine"));
        var s = (await d.Uploads.StartAsync(dep, d.ComponentId, "Dev", "bad.zip", zip.Length)).Value!;
        await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(zip), null, default);
        await d.Uploads.CompleteAsync(dep, s.SessionId);
        await d.Uploads.ProcessPendingAsync();
        var info = await d.Uploads.GetAsync(dep, s.SessionId);
        Assert.Equal(UploadStatus.Rejected, info!.Status);
        Assert.Contains(info.Errors, e => e.Path.Contains("evil"));
        Assert.Contains(info.Errors, e => e.Path.EndsWith("run.exe"));
        Assert.Null(info.PackageId);
        Assert.Contains("Package.Rejected:Failed", await d.AuditActions());
        await using var db = d.E.Db.CreateDbContext();
        Assert.Empty(db.Packages);
    }

    [Fact]
    public async Task An_extension_the_component_allows_is_accepted()
    {
        await d.SetUp();
        await using (var db = d.E.Db.CreateDbContext()) { (await db.Components.FirstAsync()).AllowedFileTypes = ".exe"; await db.SaveChangesAsync(); }
        var pkg = await d.Upload(d.Deployer("Dev"), "Dev", ("tool.exe", "MZ"), ("a.txt", "x"));
        Assert.Equal(2, pkg.EntryCount);
    }

    [Fact]
    public async Task Antivirus_modes()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        d.Av.Result = new(ScanOutcome.Infected, "Eicar");
        var zip = DeployEnv.Zip(("a.txt", "x"));
        async Task<UploadInfo> Try()
        {
            var s = (await d.Uploads.StartAsync(dep, d.ComponentId, "Dev", "a.zip", zip.Length)).Value!;
            await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(zip), null, default);
            await d.Uploads.CompleteAsync(dep, s.SessionId);
            await d.Uploads.ProcessPendingAsync();
            return (await d.Uploads.GetAsync(dep, s.SessionId))!;
        }
        var infected = await Try();
        Assert.Equal(UploadStatus.Rejected, infected.Status); Assert.Contains("Eicar", infected.Error);

        d.Av.Result = new(ScanOutcome.Unavailable, "no access");
        d.Options.AntivirusMode = "Auto";
        var auto = await Try();
        Assert.Equal(UploadStatus.Ready, auto.Status); Assert.StartsWith("ScanOnTarget", auto.AvText);

        d.Options.AntivirusMode = "Portal";
        Assert.Equal(UploadStatus.Rejected, (await Try()).Status);

        d.Options.AntivirusMode = "Agent";
        d.Av.Result = new(ScanOutcome.Infected, "must not be called");
        Assert.Equal(UploadStatus.Ready, (await Try()).Status);
    }

    [Fact]
    public async Task Abandoned_uploads_expire_and_their_files_are_removed()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var zip = DeployEnv.Zip(("a.txt", "x"));
        var s = (await d.Uploads.StartAsync(dep, d.ComponentId, "Dev", "a.zip", zip.Length)).Value!;
        await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(zip), null, default);
        d.E.Clock.Advance(TimeSpan.FromHours(25));
        await d.Uploads.CleanupAsync();
        Assert.Equal(UploadStatus.Expired, (await d.Uploads.GetAsync(dep, s.SessionId))!.Status);
        Assert.False((await d.Uploads.ReceiveChunkAsync(dep, s.SessionId, 0, new MemoryStream(zip), null, default)).Ok);
        Assert.Empty(Directory.GetDirectories(d.Store.UploadTempRoot));
    }
}
