using IisControlCenter.Contracts;

namespace IisControlCenter.Core.Tests;

public class SiteTemplateTests
{
    private static SiteTemplateDef Good() => new()
    {
        Folders = ["logs", "data/uploads"],
        Apps = [new("/api", "api")],
        VirtualDirectories = [new("/", "/files", "data/uploads"), new("/api", "/api/img", "api/img")],
        Access = [new("", FolderAccess.ReadExecute), new("data/uploads", FolderAccess.Modify)],
    };

    [Fact] public void A_good_template_is_valid() => Assert.Empty(TemplateRules.Validate(Good()));

    [Theory]
    [InlineData("..")] [InlineData("a/../b")] [InlineData(@"C:\x")] [InlineData("a:b")] [InlineData("a|b")] [InlineData("x.")] [InlineData("//")]
    public void Unsafe_relative_folders_are_refused(string f) => Assert.NotNull(TemplateRules.ValidateRelativeFolder(f, false));

    [Fact] public void The_site_folder_itself_is_allowed_only_where_empty_makes_sense()
    {
        Assert.Null(TemplateRules.ValidateRelativeFolder("", true));
        Assert.NotNull(TemplateRules.ValidateRelativeFolder("", false));
    }

    [Fact] public void Problems_are_found()
    {
        Assert.NotEmpty(TemplateRules.Validate(Good() with { Folders = ["../x"] }));
        Assert.NotEmpty(TemplateRules.Validate(Good() with { Apps = [new("/api", "a"), new("/API", "b")] }));
        Assert.NotEmpty(TemplateRules.Validate(Good() with { VirtualDirectories = [new("/nope", "/nope/x", "x")] }));
        Assert.NotEmpty(TemplateRules.Validate(Good() with { VirtualDirectories = [new("/api", "/other", "x")] }));
        Assert.NotEmpty(TemplateRules.Validate(new SiteTemplateDef { Folders = Enumerable.Range(0, 60).Select(i => "f" + i).ToList() }));
    }

    [Fact] public void Names_are_checked()
    {
        Assert.NotNull(TemplateRules.ValidateName(" "));
        Assert.NotNull(TemplateRules.ValidateName(new string('x', 81)));
        Assert.Null(TemplateRules.ValidateName("Standard web app"));
    }

    [Fact] public void The_plan_is_in_a_safe_order_and_uses_the_new_site_marker()
    {
        var plan = TemplatePlanner.Plan(Good(), new("shop", @"D:\Websites\shop", "ShopPool", [new HttpBinding("shop.test", 80)]));
        Assert.Equal([IisOp.CreatePool, IisOp.CreateSite, IisOp.CreateFolder, IisOp.CreateFolder, IisOp.CreateApplication, IisOp.CreateVirtualDirectory, IisOp.CreateVirtualDirectory, IisOp.GrantFolderAccess, IisOp.GrantFolderAccess],
            plan.Select(p => p.Op).ToArray());
        Assert.True(plan[1].CreateFolder);
        Assert.Equal(@"D:\Websites\shop\data\uploads", plan[3].PhysicalPath);
        Assert.All(plan.Where(p => p.Op is IisOp.CreateApplication or IisOp.CreateVirtualDirectory), p => Assert.Equal(IisOpCommand.NewSiteMarker, p.SiteId));
        Assert.Equal(@"D:\Websites\shop", plan[7].PhysicalPath);
        Assert.All(plan.Where(p => p.Op == IisOp.GrantFolderAccess), p => Assert.Equal("ShopPool", p.PoolName));
    }

    [Fact] public void An_empty_template_still_makes_a_pool_and_a_site()
    {
        var plan = TemplatePlanner.Plan(new SiteTemplateDef(), new("s", @"D:\w\s", "P", [new HttpBinding("s.test", 80)]));
        Assert.Equal([IisOp.CreatePool, IisOp.CreateSite], plan.Select(p => p.Op).ToArray());
    }
}
