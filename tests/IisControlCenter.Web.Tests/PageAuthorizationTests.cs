using System.Reflection;
using IisControlCenter.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace IisControlCenter.Web.Tests;

public class PageAuthorizationTests
{
    /// <summary>
    /// Component endpoints are anonymous at the endpoint level (the sign-in page and /_blazor must load),
    /// so every routable page must declare its own access rule. A new page without one would be open to anyone.
    /// </summary>
    [Fact]
    public void Every_page_declares_Authorize_or_AllowAnonymous()
    {
        var pages = typeof(App).Assembly.GetTypes().Where(t => t.GetCustomAttributes<RouteAttribute>().Any()).ToList();
        Assert.NotEmpty(pages);
        var missing = pages.Where(p => !p.GetCustomAttributes<AuthorizeAttribute>().Any() && !p.GetCustomAttributes<AllowAnonymousAttribute>().Any())
                           .Select(p => p.Name).ToList();
        Assert.True(missing.Count == 0, "Pages without an access rule: " + string.Join(", ", missing));
    }

    [Fact]
    public void Only_the_expected_pages_are_anonymous()
    {
        var anon = typeof(App).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any() && t.GetCustomAttributes<AllowAnonymousAttribute>().Any())
            .Select(t => t.Name).OrderBy(x => x).ToList();
        Assert.Equal(["AccessDenied", "DevLogin", "Error", "NotFound", "SignedOut"], anon);
    }
}
