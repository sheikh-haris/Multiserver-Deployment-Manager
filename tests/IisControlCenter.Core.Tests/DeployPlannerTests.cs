using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Core.Tests;

public class RelativePathsTests
{
    [Theory]
    [InlineData("index.html", "index.html")]
    [InlineData("bin/App.dll", "bin/App.dll")]
    [InlineData(@"bin\App.dll", "bin/App.dll")]
    [InlineData("./a//b/./c.txt", "a/b/c.txt")]
    [InlineData("Views/Shared/_Layout.cshtml", "Views/Shared/_Layout.cshtml")]
    [InlineData("a b/c d.txt", "a b/c d.txt")]
    public void Accepts_normal_paths(string input, string expected) => Assert.Equal(expected, RelativePaths.Normalize(input, out var e) ?? "FAIL:" + e);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("../x.dll")]
    [InlineData("a/../../x.dll")]
    [InlineData(@"a\..\..\x.dll")]
    [InlineData("a/b/..")]
    [InlineData("file.txt:stream")]
    [InlineData("file.txt::$DATA")]
    [InlineData("CON")]
    [InlineData("a/nul.txt")]
    [InlineData("a/COM1.log")]
    [InlineData("a/b.")]
    [InlineData("a/b ")]
    [InlineData("a/ b")]
    [InlineData("a/b*.txt")]
    [InlineData("a/b?.txt")]
    [InlineData("a/<x>.txt")]
    [InlineData("a/b\0c")]
    [InlineData("a/b\tc")]
    [InlineData("./")]
    public void Rejects_unsafe_paths(string? input)
    {
        Assert.Null(RelativePaths.Normalize(input, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Rejects_overlong_names()
    {
        Assert.Null(RelativePaths.Normalize(new string('a', 256) + ".txt", out _));
        Assert.Null(RelativePaths.Normalize(string.Join('/', Enumerable.Repeat(new string('a', 100), 11)), out _));
        Assert.NotNull(RelativePaths.Normalize(new string('a', 200) + ".txt", out _));
    }
}

public class ProtectedPathsTests
{
    private static readonly ProtectedPaths P = ProtectedPaths.Defaults;

    [Theory]
    [InlineData("web.config", true)]
    [InlineData("WEB.CONFIG", true)]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Production.json", true)]
    [InlineData("Views/web.config", false)]            // root only unless the pattern says **/
    [InlineData("bin/appsettings.json", false)]
    [InlineData("web.config.bak", false)]
    [InlineData("myappsettings.json", false)]
    public void Config_patterns_match_the_root_only_by_default(string path, bool expected) => Assert.Equal(expected, P.IsConfigFile(path));

    [Theory]
    [InlineData("uploads/a.png", true)]
    [InlineData("Uploads/sub/deep/a.png", true)]
    [InlineData("uploads", true)]
    [InlineData("generated-files/x.pdf", true)]
    [InlineData("uploads2/a.png", false)]
    [InlineData("myuploads/a.png", false)]
    [InlineData("bin/uploads/a.png", false)]
    [InlineData("index.html", false)]
    public void Data_folders_protect_everything_below_them(string path, bool expected) => Assert.Equal(expected, P.IsInDataFolder(path));

    [Fact]
    public void Double_star_matches_at_any_depth()
    {
        var p = new ProtectedPaths(["**/web.config", "**/secrets/*.json"], ["**/App_Data"]);
        Assert.True(p.IsConfigFile("web.config"));
        Assert.True(p.IsConfigFile("Views/web.config"));
        Assert.True(p.IsConfigFile("a/b/web.config"));
        Assert.True(p.IsConfigFile("x/secrets/k.json"));
        Assert.False(p.IsConfigFile("x/secrets/deep/k.json"));
        Assert.True(p.IsInDataFolder("App_Data/db.mdf"));
        Assert.True(p.IsInDataFolder("area/App_Data/db.mdf"));
        Assert.False(p.IsInDataFolder("area/App_Data2/db.mdf"));
    }

    [Fact]
    public void Slashes_and_blank_entries_are_cleaned()
    {
        var p = new ProtectedPaths(["  ", "\\web.config"], ["\\uploads\\", "", "uploads/"]);
        Assert.Equal(["web.config"], p.ConfigPatterns);
        Assert.Equal(["uploads"], p.DataFolders);
        Assert.True(p.IsInDataFolder("uploads/x"));
    }

    [Fact]
    public void Regex_characters_in_patterns_are_literal()
    {
        var p = new ProtectedPaths(["a+b(1).config"], []);
        Assert.True(p.IsConfigFile("a+b(1).config"));
        Assert.False(p.IsConfigFile("aab1.config"));
    }
}

public class DeployPlannerTests
{
    private static PackageFile F(string path, string hash = "h1", long size = 10) => new(path, size, hash);
    private static LiveFile L(string path, string? hash = "h1", long size = 10) => new(path, size, hash);
    private static Dictionary<string, LiveFile> Live(params LiveFile[] l) => l.ToDictionary(x => x.Path, RelativePaths.Comparer);

    private static DeployPlan Plan(PackageFile[] pkg, LiveFile[] live, DeployMode mode = DeployMode.Overlay, string[]? forced = null, LiveFile[]? all = null, ProtectedPaths? pp = null) =>
        DeployPlanner.Plan(pkg, Live(live), all ?? live, mode, pp ?? ProtectedPaths.Defaults, forced is null ? null : new HashSet<string>(forced, RelativePaths.Comparer));

    private static FileAction ActionOf(DeployPlan plan, string path) => plan.Changes.Single(c => c.Path == path).Action;

    [Fact]
    public void New_changed_and_unchanged_files()
    {
        var plan = Plan([F("a.txt"), F("b.txt", "NEW"), F("c.txt")], [L("b.txt", "OLD"), L("c.txt", "h1")]);
        Assert.Equal(FileAction.Add, ActionOf(plan, "a.txt"));
        Assert.Equal(FileAction.Replace, ActionOf(plan, "b.txt"));
        Assert.Equal(FileAction.Unchanged, ActionOf(plan, "c.txt"));
    }

    [Fact]
    public void Hash_comparison_ignores_case_and_a_missing_live_hash_means_changed()
    {
        Assert.Equal(FileAction.Unchanged, ActionOf(Plan([F("a", "ABCD")], [L("a", "abcd")]), "a"));
        Assert.Equal(FileAction.Replace, ActionOf(Plan([F("a", "ABCD")], [L("a", null)]), "a"));
    }

    [Fact]
    public void Overlay_never_deletes()
    {
        var plan = Plan([F("a.txt")], [L("a.txt"), L("old.dll")], DeployMode.Overlay);
        Assert.Equal(0, plan.Count(FileAction.Delete));
    }

    [Fact]
    public void Full_replace_deletes_files_not_in_the_package()
    {
        var plan = Plan([F("a.txt")], [L("a.txt"), L("old.dll"), L("bin/x.dll")], DeployMode.FullReplace);
        Assert.Equal(["bin/x.dll", "old.dll"], plan.Deletes.Select(d => d.Path).OrderBy(x => x));
    }

    [Fact]
    public void Full_replace_never_deletes_protected_config_or_data_even_if_missing_from_the_package()
    {
        var plan = Plan([F("a.txt")], [L("a.txt"), L("web.config"), L("appsettings.Prod.json"), L("uploads/u1.png"), L("uploads/deep/u2.png"), L("old.dll")], DeployMode.FullReplace);
        Assert.Equal(["old.dll"], plan.Deletes.Select(d => d.Path));
    }

    [Fact]
    public void Protected_config_is_copied_when_missing_and_skipped_when_present()
    {
        var plan = Plan([F("web.config", "NEW"), F("appsettings.json", "NEW")], [L("appsettings.json", "OLD")]);
        Assert.Equal(FileAction.Add, ActionOf(plan, "web.config"));                  // first-time deployment
        Assert.Equal(FileAction.SkipProtectedConfig, ActionOf(plan, "appsettings.json"));
    }

    [Fact]
    public void Force_overwrite_works_only_for_protected_config_files_that_exist()
    {
        var plan = Plan([F("web.config", "NEW"), F("a.txt", "NEW")], [L("web.config", "OLD"), L("a.txt", "OLD")], forced: ["web.config", "a.txt"]);
        Assert.Equal(FileAction.ForceOverwrite, ActionOf(plan, "web.config"));
        Assert.Equal(FileAction.Replace, ActionOf(plan, "a.txt"));                    // not protected: normal change, force is irrelevant
    }

    [Fact]
    public void Data_folders_cannot_be_forced_and_are_not_written_even_when_absent()
    {
        var plan = Plan([F("uploads/readme.txt"), F("uploads/a.png", "NEW")], [L("uploads/a.png", "OLD")], forced: ["uploads/a.png", "uploads/readme.txt"]);
        Assert.All(plan.Changes, c => Assert.Equal(FileAction.SkipProtectedData, c.Action));
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void Path_comparison_ignores_case_like_the_windows_file_system()
    {
        var plan = Plan([F("Bin/App.dll", "NEW")], [L("bin/app.dll", "OLD")]);
        Assert.Equal(FileAction.Replace, plan.Changes.Single().Action);
        var full = Plan([F("Bin/App.dll")], [L("bin/app.dll")], DeployMode.FullReplace);
        Assert.Empty(full.Deletes);                                                  // same file, only the case differs
    }

    [Fact]
    public void Writes_and_deletes_helpers()
    {
        var plan = Plan([F("a"), F("b", "N"), F("web.config", "N")], [L("b", "O"), L("web.config", "O"), L("gone")], DeployMode.FullReplace, forced: ["web.config"]);
        Assert.Equal(["a", "b", "web.config"], plan.Writes.Select(w => w.Path).OrderBy(x => x));
        Assert.Equal(["gone"], plan.Deletes.Select(d => d.Path));
    }

    [Theory]
    [InlineData(0, 100, null)]
    [InlineData(5, 100, null)]
    [InlineData(20, 1000, null)]       // exactly 20 and 2%: fine
    [InlineData(21, 1000, "DELETE 21")] // more than 20
    [InlineData(11, 100, "DELETE 11")]  // more than 10 %
    [InlineData(10, 100, null)]         // exactly 10 %
    public void Deletion_warning_thresholds(int deletes, int total, string? expectedFragment)
    {
        var plan = new DeployPlan(Enumerable.Range(0, deletes).Select(i => new PlannedChange($"f{i}", FileAction.Delete, 1, null, null, null)).ToList());
        var w = DeployPlanner.DeletionWarning(plan, total);
        if (expectedFragment is null) Assert.Null(w); else Assert.Contains(expectedFragment, w);
    }
}

public class SecretMaskerTests
{
    private static string M(string s) => SecretMasker.Mask(s, "salt");

    [Fact]
    public void Masks_connection_strings_in_xml_but_keeps_the_harmless_parts()
    {
        var r = M("<add name=\"Db\" connectionString=\"Server=sql01;Database=Pay;User ID=app;Password=Hunter2!;Encrypt=true\" />");
        Assert.DoesNotContain("Hunter2", r);
        Assert.Contains("Server=sql01", r);
        Assert.Contains("Database=Pay", r);
        Assert.Contains("User ID=app", r);
        Assert.Contains("••••", r);
    }

    [Fact]
    public void Masks_json_secrets_by_key_name()
    {
        var r = M("{ \"Logging\": { \"Level\": \"Info\" }, \"Smtp\": { \"Host\": \"mail\", \"Password\": \"p@ss w0rd\" }, \"ApiKey\": \"abc123\", \"ClientSecret\": \"zzz\" }");
        Assert.DoesNotContain("p@ss", r); Assert.DoesNotContain("abc123", r); Assert.DoesNotContain("zzz", r);
        Assert.Contains("\"Host\": \"mail\"", r);
        Assert.Contains("\"Level\": \"Info\"", r);
    }

    [Fact]
    public void Masks_json_connection_strings_but_only_the_secret_part()
    {
        var r = M("{\"ConnectionStrings\":{\"Default\":\"Server=a;Database=b;Password=secret1;\"}}");
        Assert.DoesNotContain("secret1", r);
        Assert.Contains("Server=a", r);
    }

    [Fact]
    public void Masks_app_settings_add_key_value_and_xml_elements()
    {
        var r = M("<appSettings><add key=\"PaymentApiKey\" value=\"live_9f8e7d\"/><add key=\"Title\" value=\"My Site\"/></appSettings><smtp><password>mailpw</password></smtp>");
        Assert.DoesNotContain("live_9f8e7d", r); Assert.DoesNotContain("mailpw", r);
        Assert.Contains("value=\"My Site\"", r);
    }

    [Fact]
    public void Masks_machine_keys_and_long_opaque_values()
    {
        var r = M("<machineKey validationKey=\"AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899\" decryptionKey=\"0123456789ABCDEF0123456789ABCDEF\" />" +
                  "<x token: \"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9abcdefghijkl\" />");
        Assert.DoesNotContain("AABBCCDD", r); Assert.DoesNotContain("0123456789ABCDEF", r); Assert.DoesNotContain("eyJhbGci", r);
    }

    [Fact]
    public void Leaves_ordinary_config_alone()
    {
        const string text = "<configuration><system.web><compilation debug=\"false\" targetFramework=\"4.8\"/><authentication mode=\"Windows\"/><httpRuntime maxRequestLength=\"4096\"/></system.web></configuration>";
        Assert.Equal(text, M(text));
        const string json = "{\"AllowedHosts\":\"*\",\"Logging\":{\"LogLevel\":{\"Default\":\"Warning\"}},\"Path\":\"D:\\\\Websites\\\\pay\"}";
        Assert.Equal(json, M(json));
    }

    [Fact]
    public void Same_secret_gives_the_same_token_and_a_changed_secret_a_different_one()
    {
        var a = M("{\"Password\":\"one\"}"); var b = M("{\"Password\":\"one\"}"); var c = M("{\"Password\":\"two\"}");
        Assert.Equal(a, b); Assert.NotEqual(a, c);
        Assert.NotEqual(SecretMasker.Mask("{\"Password\":\"one\"}", "salt1"), SecretMasker.Mask("{\"Password\":\"one\"}", "salt2"));   // salt differs per run
    }

    [Fact]
    public void Masking_is_idempotent_and_handles_empty_input()
    {
        var once = M("{\"Password\":\"one\"}");
        Assert.Equal(once, M(once));
        Assert.Equal("", M(""));
        Assert.Equal("", SecretMasker.Mask(null!, "s"));
        Assert.Equal("{\"Password\":\"\"}", M("{\"Password\":\"\"}"));
    }

    [Theory]
    [InlineData("Password=abc;")]
    [InlineData("pwd=abc;")]
    [InlineData("Account Key=abc;")]
    public void Connection_string_variants(string part)
    {
        var r = M($"<add connectionString=\"Server=s;{part}X=y\"/>");
        Assert.DoesNotContain("abc", r);
        Assert.Contains("Server=s", r);
    }
}
