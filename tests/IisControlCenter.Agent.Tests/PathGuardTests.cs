using IisControlCenter.Agent.Security;

namespace IisControlCenter.Agent.Tests;

public class PathGuardTests
{
    private static readonly string[] Roots = [@"D:\Websites"];

    [Theory]
    [InlineData(@"D:\Websites\App", @"d:\websites\app")]
    [InlineData(@"d:/websites/app/", @"d:\websites\app")]
    [InlineData(@"D:\Websites\.\App\.\x", @"d:\websites\app\x")]
    [InlineData(@"D:\\Websites\\\App", @"d:\websites\app")]
    [InlineData(@"D:\", @"d:\")]
    public void Normalizes_paths(string input, string expected) => Assert.Equal(expected, PathGuard.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"websites\app")]               // relative
    [InlineData(@"\Websites\app")]              // drive relative
    [InlineData(@"D:Websites")]                 // drive relative
    [InlineData(@"\\server\share\x")]           // UNC
    [InlineData(@"\\?\C:\Windows")]             // device path
    [InlineData(@"D:\Websites\..\Windows")]     // traversal
    [InlineData(@"D:\Websites\a\..\..\x")]
    [InlineData(@"D:\Websites\a:stream")]       // alternate data stream
    [InlineData(@"D:\Websites\file.txt::$DATA")]
    [InlineData(@"D:\Websites\CON")]            // reserved device names
    [InlineData(@"D:\Websites\nul.txt")]
    [InlineData(@"D:\Websites\com1")]
    [InlineData(@"D:\Websites\app.")]           // Windows drops trailing dots and spaces
    [InlineData(@"D:\Websites\app ")]
    [InlineData(@"D:\Websites\a*b")]
    [InlineData(@"D:\Websites\a?b")]
    [InlineData("D:\\Websites\\a\0b")]
    public void Rejects_unsafe_or_non_absolute_paths(string? input) => Assert.Null(PathGuard.Normalize(input));

    [Theory]
    [InlineData(@"D:\Websites")]
    [InlineData(@"D:\Websites\payments")]
    [InlineData(@"E:\Sites")]
    public void Accepts_normal_roots(string root) => Assert.Null(PathGuard.ValidateRoot(root));

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)\App")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users\admin")]
    [InlineData(@"\\server\share")]
    [InlineData(@"relative")]
    [InlineData(@"D:\Websites\..")]
    public void Rejects_dangerous_roots(string root) => Assert.NotNull(PathGuard.ValidateRoot(root));

    [Theory]
    [InlineData(@"D:\Websites", true)]                  // the root itself
    [InlineData(@"D:\Websites\payments\web", true)]
    [InlineData(@"d:\WEBSITES\Payments", true)]         // case-insensitive
    [InlineData(@"D:/Websites/payments", true)]
    [InlineData(@"D:\Websites2\payments", false)]       // prefix is not enough
    [InlineData(@"D:\WebsitesEvil", false)]
    [InlineData(@"D:\Other", false)]
    [InlineData(@"E:\Websites", false)]                 // different drive
    [InlineData(@"D:\Websites\..\Other", false)]
    [InlineData(@"D:\Websites\..\Websites\x", false)]   // even harmless traversal is refused, never resolved
    [InlineData(@"D:\Websites\a:ads", false)]
    [InlineData(@"\\?\D:\Websites\x", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(null, false)]
    public void Decides_whether_a_path_is_under_an_allowed_root(string? path, bool expected) =>
        Assert.Equal(expected, PathGuard.IsUnderAnyRoot(path, Roots));

    [Fact]
    public void Several_roots_work_and_invalid_roots_never_allow_anything()
    {
        Assert.True(PathGuard.IsUnderAnyRoot(@"E:\Sites\a", [@"D:\Websites", @"E:\Sites"]));
        Assert.False(PathGuard.IsUnderAnyRoot(@"C:\Windows\x", [@"C:\Windows"]));    // forbidden root is ignored
        Assert.False(PathGuard.IsUnderAnyRoot(@"D:\x", [@"D:\"]));                   // whole drive is ignored
        Assert.False(PathGuard.IsUnderAnyRoot(@"D:\Websites", []));
    }

    [Theory]
    [InlineData(@"D:\Websites\shop", true)]
    [InlineData(@"d:/websites/shop/api", true)]
    [InlineData(@"D:\Websites", false)]            // the root itself
    [InlineData(@"D:\Websites\", false)]
    [InlineData(@"D:\Websites\..\Windows", false)]
    [InlineData(@"D:\Websites2\x", false)]
    [InlineData(@"E:\Websites\shop", false)]
    [InlineData(@"\\server\share\shop", false)]
    [InlineData(@"D:\Websites\con\x", false)]
    public void Creating_things_needs_a_path_strictly_below_a_root(string path, bool ok) =>
        Assert.Equal(ok, PathGuard.IsStrictlyUnderAnyRoot(path, [@"D:\Websites"]));
}
