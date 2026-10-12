using System.IO.Compression;
using System.Text;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;

namespace IisControlCenter.Core.Tests;

public class ZipValidatorTests
{
    private static MemoryStream Zip(params (string Name, string Content)[] entries) => ZipBytes(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    private static MemoryStream ZipBytes(params (string Name, byte[] Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = z.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open(); s.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    private static ZipValidation V(Stream s, ZipPolicy? p = null) => ZipValidator.Validate(s, p ?? new ZipPolicy());

    [Fact]
    public void Valid_package_returns_a_manifest_with_sha256_per_file()
    {
        var r = V(Zip(("index.html", "<h1>hi</h1>"), ("bin/App.dll", "dll"), ("web.config", "<configuration/>")));
        Assert.True(r.IsValid);
        Assert.Equal(["bin/App.dll", "index.html", "web.config"], r.Files.Select(f => f.Path));
        var idx = r.Files.Single(f => f.Path == "index.html");
        Assert.Equal(11, idx.Size);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("<h1>hi</h1>"))), idx.Sha256);
        Assert.Equal(11 + 3 + 16, r.TotalUncompressedBytes);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("/etc/cron.d/x")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("C:\\Windows\\x.dll")]
    [InlineData("a.txt:hidden")]
    [InlineData("CON")]
    [InlineData("a/aux.txt")]
    [InlineData("a/trailing.")]
    public void Zip_slip_and_unsafe_names_are_rejected(string name)
    {
        var r = V(Zip(("ok.txt", "x"), (name, "x")));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Path == name && e.Message.Contains("Unsafe"));
    }

    [Theory]
    [InlineData("tools/setup.exe")]
    [InlineData("run.bat")]
    [InlineData("run.CMD")]
    [InlineData("x/y.ps1")]
    [InlineData("a.msi")]
    public void Blocked_file_types_are_rejected_unless_the_component_allows_them(string name)
    {
        var r = V(Zip((name, "x")));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Message.Contains("blocked"));
        var ext = Path.GetExtension(name).ToLowerInvariant();
        Assert.True(V(Zip((name, "x")), new ZipPolicy(AllowedExtraExtensions: new HashSet<string> { ext })).IsValid);
    }

    [Fact]
    public void Dlls_and_aspx_are_allowed_because_deploying_them_is_the_job() =>
        Assert.True(V(Zip(("bin/a.dll", "x"), ("default.aspx", "x"), ("a.ps1.txt", "x"))).IsValid);

    [Fact]
    public void Duplicate_names_ignoring_case_are_rejected()
    {
        var r = V(Zip(("a/File.txt", "1"), ("a/file.TXT", "2")));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Message.Contains("Duplicate"));
    }

    [Fact]
    public void Too_many_entries_are_rejected_before_anything_is_read()
    {
        var r = V(Zip(Enumerable.Range(0, 11).Select(i => ($"f{i}.txt", "x")).ToArray()), new ZipPolicy(MaxEntries: 10));
        Assert.False(r.IsValid);
        Assert.Contains("limit", r.Errors[0].Message);
    }

    [Fact]
    public void Unpacked_size_is_limited_by_actual_bytes_not_the_declared_size()
    {
        var big = new byte[3 * 1024 * 1024];
        var r = V(ZipBytes(("a.bin", big), ("b.bin", big)), new ZipPolicy(MaxUncompressedBytes: 4 * 1024 * 1024));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Message.Contains("zip bomb"));
    }

    [Fact]
    public void Extreme_compression_ratio_is_rejected()
    {
        var zeros = new byte[20 * 1024 * 1024];     // compresses to a few KB
        var r = V(ZipBytes(("zeros.bin", zeros)), new ZipPolicy(MaxCompressionRatio: 100, RatioCheckMinBytes: 1024 * 1024));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Message.Contains("compression ratio"));
    }

    [Fact]
    public void Not_a_zip_and_empty_zip_are_rejected_with_clear_messages()
    {
        Assert.Contains("not a valid zip", V(new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip"))).Errors[0].Message);
        Assert.Contains("no files", V(Zip()).Errors[0].Message);
        Assert.False(V(new MemoryStream()).IsValid);
    }

    [Fact]
    public void Directory_entries_are_ignored_and_a_single_top_folder_gets_a_warning()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            z.CreateEntry("site/");
            foreach (var n in new[] { "site/a.txt", "site/b.txt" }) { using var s = z.CreateEntry(n).Open(); s.Write([1]); }
        }
        ms.Position = 0;
        var r = V(ms);
        Assert.True(r.IsValid);
        Assert.Equal(2, r.Files.Count);
        Assert.Contains(r.Warnings, w => w.Contains("site/"));
        Assert.Empty(V(Zip(("a.txt", "1"), ("b/c.txt", "2"))).Warnings);
    }

    [Fact]
    public void Symbolic_links_are_rejected()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = z.CreateEntry("link");
            e.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var s = e.Open(); s.Write(Encoding.UTF8.GetBytes("/etc/passwd"));
        }
        ms.Position = 0;
        Assert.Contains(V(ms).Errors, e => e.Message.Contains("Symbolic"));
    }

    [Fact]
    public void All_errors_are_reported_not_only_the_first()
    {
        var r = V(Zip(("../a.txt", "x"), ("b.exe", "x"), ("c.bat", "x")));
        Assert.Equal(3, r.Errors.Count);
    }

    [Fact]
    public void Backslash_entry_names_are_treated_as_folders()
    {
        var r = V(Zip(("bin\\App.dll", "x")));
        Assert.Equal("bin/App.dll", r.Files.Single().Path);
    }
}

public class TextDiffTests
{
    [Fact]
    public void Shows_added_removed_and_unchanged_lines()
    {
        var d = TextDiff.Diff("a\nb\nc\n", "a\nB\nc\nd\n")!;
        Assert.Equal(["Same:a", "Removed:b", "Added:B", "Same:c", "Added:d"], d.Select(l => $"{l.Kind}:{l.Text}"));
    }

    [Fact] public void Identical_text_has_no_changes() => Assert.All(TextDiff.Diff("a\nb", "a\nb")!, l => Assert.Equal(DiffKind.Same, l.Kind));

    [Fact]
    public void Line_endings_do_not_matter()
    {
        var d = TextDiff.Diff("a\r\nb\r\n", "a\nb\n")!;
        Assert.All(d, l => Assert.Equal(DiffKind.Same, l.Kind));
    }

    [Fact]
    public void Too_large_inputs_return_null()
    {
        var big = string.Join('\n', Enumerable.Range(0, TextDiff.MaxLines + 1));
        Assert.Null(TextDiff.Diff(big, "x"));
    }

    [Fact]
    public void Collapse_keeps_changes_with_context_and_marks_the_gaps()
    {
        var before = string.Join('\n', Enumerable.Range(1, 30).Select(i => "l" + i));
        var after = before.Replace("l15", "CHANGED");
        var c = TextDiff.Collapse(TextDiff.Diff(before, after)!, 2);
        Assert.Contains(c, l => l.Kind == DiffKind.Added && l.Text == "CHANGED");
        Assert.Contains(c, l => l.Text == "…");
        Assert.True(c.Count < 12);
    }
}

public class StateMachineTests
{
    [Theory]
    [InlineData(RequestStatus.Draft, RequestStatus.Submitted)]
    [InlineData(RequestStatus.Submitted, RequestStatus.Approved)]
    [InlineData(RequestStatus.Submitted, RequestStatus.Scheduled)]
    [InlineData(RequestStatus.Submitted, RequestStatus.Rejected)]
    [InlineData(RequestStatus.Submitted, RequestStatus.Expired)]
    [InlineData(RequestStatus.Approved, RequestStatus.Running)]
    [InlineData(RequestStatus.Scheduled, RequestStatus.Running)]
    [InlineData(RequestStatus.Running, RequestStatus.Succeeded)]
    [InlineData(RequestStatus.Running, RequestStatus.Failed)]
    [InlineData(RequestStatus.Running, RequestStatus.RolledBack)]
    [InlineData(RequestStatus.Running, RequestStatus.NeedsReapproval)]
    [InlineData(RequestStatus.NeedsReapproval, RequestStatus.Submitted)]
    public void Legal_transitions(RequestStatus from, RequestStatus to)
    {
        Assert.True(RequestStateMachine.CanTransition(from, to));
        RequestStateMachine.EnsureCanTransition(from, to);
    }

    [Theory]
    [InlineData(RequestStatus.Submitted, RequestStatus.Running)]        // must be approved first
    [InlineData(RequestStatus.Submitted, RequestStatus.Succeeded)]
    [InlineData(RequestStatus.Draft, RequestStatus.Approved)]
    [InlineData(RequestStatus.Running, RequestStatus.Cancelled)]        // cancel only until execution starts
    [InlineData(RequestStatus.Running, RequestStatus.Approved)]
    [InlineData(RequestStatus.Succeeded, RequestStatus.Running)]
    [InlineData(RequestStatus.Rejected, RequestStatus.Approved)]
    [InlineData(RequestStatus.Cancelled, RequestStatus.Submitted)]
    [InlineData(RequestStatus.RolledBack, RequestStatus.Running)]
    public void Illegal_transitions_are_refused(RequestStatus from, RequestStatus to)
    {
        Assert.False(RequestStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => RequestStateMachine.EnsureCanTransition(from, to));
    }

    [Fact]
    public void Terminal_states_have_no_way_out_and_every_state_is_covered()
    {
        foreach (var s in new[] { RequestStatus.Succeeded, RequestStatus.Failed, RequestStatus.RolledBack, RequestStatus.Rejected, RequestStatus.Expired, RequestStatus.Cancelled })
        {
            Assert.True(RequestStateMachine.IsTerminal(s));
            foreach (var t in Enum.GetValues<RequestStatus>()) Assert.False(RequestStateMachine.CanTransition(s, t));
        }
        Assert.Equal(12, Enum.GetValues<RequestStatus>().Length);
    }

    [Fact]
    public void Cancel_is_possible_only_before_execution_starts()
    {
        Assert.All(new[] { RequestStatus.Draft, RequestStatus.Submitted, RequestStatus.Approved, RequestStatus.Scheduled, RequestStatus.NeedsReapproval },
            s => Assert.True(RequestStateMachine.CanCancel(s)));
        Assert.All(new[] { RequestStatus.Running, RequestStatus.Succeeded, RequestStatus.Failed },
            s => Assert.False(RequestStateMachine.CanCancel(s)));
    }
}

public class ApprovalRulesTests
{
    private static ApprovalCheck Check(string requester = "req", string approver = "app", bool role = true, bool self = false, string[]? existing = null, int required = 1, RequestStatus status = RequestStatus.Submitted) =>
        ApprovalRules.Check(status, requester, approver, role, self, existing ?? [], required);

    [Fact] public void Normal_approval_completes_a_single_approver_request() { var c = Check(); Assert.True(c.Allowed); Assert.True(c.Completes); Assert.False(c.IsSelfApproval); }

    [Fact]
    public void Requester_cannot_approve_their_own_request_even_with_the_role()
    {
        var c = Check(requester: "bob", approver: "BOB");
        Assert.False(c.Allowed);
        Assert.True(c.IsSelfApproval);
        Assert.Contains("own request", c.Reason);
    }

    [Fact]
    public void Self_approval_is_allowed_only_when_the_project_switched_it_on_and_it_is_flagged()
    {
        var c = Check(requester: "bob", approver: "bob", self: true);
        Assert.True(c.Allowed); Assert.True(c.IsSelfApproval); Assert.True(c.Completes);
    }

    [Fact] public void Approver_role_is_required() => Assert.False(Check(role: false).Allowed);

    [Fact]
    public void Two_approver_requests_need_two_different_people()
    {
        var first = Check(required: 2);
        Assert.True(first.Allowed); Assert.False(first.Completes);
        Assert.False(Check(approver: "a1", existing: ["a1"], required: 2).Allowed);                 // same person twice
        Assert.False(Check(approver: "A1", existing: ["a1"], required: 2).Allowed);                 // case-insensitive
        var second = Check(approver: "a2", existing: ["a1"], required: 2);
        Assert.True(second.Allowed); Assert.True(second.Completes);
    }

    [Theory]
    [InlineData(RequestStatus.Draft)] [InlineData(RequestStatus.Approved)] [InlineData(RequestStatus.Running)] [InlineData(RequestStatus.Rejected)]
    public void Only_submitted_requests_can_be_approved(RequestStatus s) => Assert.False(Check(status: s).Allowed);

    [Theory]
    [InlineData(false, 2, 1)] [InlineData(true, 1, 1)] [InlineData(true, 2, 2)] [InlineData(true, 9, 2)] [InlineData(true, 0, 1)]
    public void Required_approvers(bool direct, int project, int expected) => Assert.Equal(expected, ApprovalRules.Required(direct, project));
}

public class SubmitRulesTests
{
    private static SubmitInput Input(Func<SubmitInput, SubmitInput>? f = null)
    {
        var i = new SubmitInput(RequestType.Deploy, false, false, false, false, "PR-123", null, null, null, 1, 1, true, DeployMode.Overlay, true, true, [], ["web.config"]);
        return f is null ? i : f(i);
    }
    private static List<string> V(Func<SubmitInput, SubmitInput>? f = null) => SubmitRules.Validate(Input(f));

    [Fact] public void A_normal_request_is_valid() => Assert.Empty(V());

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public void Source_reference_is_required(string? s) => Assert.Contains(V(i => i with { SourceReference = s }), e => e.Contains("source reference"));

    [Fact] public void Source_reference_has_a_length_limit() => Assert.NotEmpty(V(i => i with { SourceReference = new string('x', 201) }));

    [Fact] public void Dirty_package_blocks_submission() => Assert.Contains(V(i => i with { PackageClean = false }), e => e.Contains("virus scan"));

    [Fact] public void No_target_selected_is_an_error() => Assert.Contains(V(i => i with { SelectedTargets = 0 }), e => e.Contains("at least one"));

    [Fact]
    public void Deploying_to_only_some_servers_needs_a_reason_but_all_servers_does_not()
    {
        Assert.Contains(V(i => i with { ActiveTargetsInEnvironment = 3, SelectedTargets = 2 }), e => e.Contains("different code"));
        Assert.Empty(V(i => i with { ActiveTargetsInEnvironment = 3, SelectedTargets = 2, SingleServerReason = "canary" }));
        Assert.Empty(V(i => i with { ActiveTargetsInEnvironment = 3, SelectedTargets = 3 }));
        Assert.Empty(V(i => i with { ActiveTargetsInEnvironment = 1, SelectedTargets = 1 }));
    }

    [Fact]
    public void Direct_to_prod_needs_a_reason_and_is_blocked_when_the_component_requires_staging()
    {
        Assert.Contains(V(i => i with { IsProduction = true, IsDirectToProd = true }), e => e.Contains("reason is required"));
        Assert.Empty(V(i => i with { IsProduction = true, IsDirectToProd = true, DirectToProdReason = "hotfix" }));
        Assert.Contains(V(i => i with { IsProduction = true, IsDirectToProd = true, DirectToProdReason = "x", ComponentRequiresStagingBeforeProd = true }), e => e.Contains("Staging first"));
        Assert.Contains(V(i => i with { IsProduction = false, IsDirectToProd = true, DirectToProdReason = "x" }), e => e.Contains("only applies"));
    }

    [Fact] public void Promote_only_targets_production() => Assert.NotEmpty(V(i => i with { IsPromote = true, IsProduction = false }));

    [Fact]
    public void Full_replace_is_blocked_until_the_protected_list_is_confirmed()
    {
        Assert.Contains(V(i => i with { Mode = DeployMode.FullReplace, ProtectedListConfirmed = false }), e => e.Contains("confirmed"));
        Assert.Empty(V(i => i with { Mode = DeployMode.FullReplace, ProtectedListConfirmed = true }));
        Assert.Empty(V(i => i with { Mode = DeployMode.Overlay, ProtectedListConfirmed = false }));
    }

    [Fact]
    public void Forced_files_need_a_reason_exist_and_be_protected_config_files()
    {
        Assert.Empty(V(i => i with { ForcedFiles = [new("web.config", "new binding")] }));
        Assert.Contains(V(i => i with { ForcedFiles = [new("web.config", " ")] }), e => e.Contains("needs a reason"));
        Assert.Contains(V(i => i with { ForcedFiles = [new("index.html", "x")] }), e => e.Contains("cannot be force-overwritten"));
        Assert.Contains(V(i => i with { ForcedFiles = [new("uploads/a.png", "x")] }), e => e.Contains("cannot be force-overwritten"));
        Assert.Contains(V(i => i with { ForcedFiles = [new("web.config", "a"), new("WEB.CONFIG", "b")] }), e => e.Contains("twice"));
    }

    [Fact] public void Rollback_requests_do_not_need_a_source_reference() => Assert.Empty(V(i => i with { Type = RequestType.Rollback, SourceReference = null }));
}
