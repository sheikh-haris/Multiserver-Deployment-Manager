using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Core.Tests;

public class NestedFoldersTests
{
    [Fact]
    public void Folders_inside_the_target_become_relative_data_folders()
    {
        var r = NestedFolders.RelativeInside(@"D:\Websites\shop", [@"D:\Websites\shop\api", @"d:/websites/SHOP/admin/v2/", @"D:\Websites\shop"]);
        Assert.Equal(["admin/v2/", "api/"], r);
    }

    [Theory]
    [InlineData(@"D:\Websites\shop", @"D:\Websites\shop2\api")]       // name prefix is not a parent
    [InlineData(@"D:\Websites\shop", @"D:\Websites")]                  // the parent itself
    [InlineData(@"D:\Websites\shop", @"E:\Websites\shop\api")]
    [InlineData(@"D:\Websites\shop", @"D:\Websites\shop\..\other")]
    [InlineData(@"", @"D:\x")]
    public void Anything_else_is_ignored(string target, string other) => Assert.Empty(NestedFolders.RelativeInside(target, [other]));

    [Fact]
    public void The_result_is_understood_by_the_protected_path_rules()
    {
        var pp = new ProtectedPaths([], NestedFolders.RelativeInside(@"D:\s", [@"D:\s\api"]));
        Assert.True(pp.IsInDataFolder("api/bin/app.dll"));
        Assert.False(pp.IsInDataFolder("apiary/x.txt"));
        var plan = DeployPlanner.Plan([new PackageFile("api/new.dll", 1, "AA"), new PackageFile("index.html", 1, "BB")], new Dictionary<string, LiveFile>(),
            [new LiveFile("api/keep.dll", 1, null), new LiveFile("old.js", 1, null)], DeployMode.FullReplace, pp);
        Assert.Contains(plan.Changes, c => c.Path == "api/new.dll" && c.Action == FileAction.SkipProtectedData);
        Assert.Equal(["old.js"], plan.Deletes.Select(d => d.Path));       // the child's files are never deleted
    }
}
