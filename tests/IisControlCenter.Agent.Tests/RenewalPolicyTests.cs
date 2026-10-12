using IisControlCenter.Agent.Security;

namespace IisControlCenter.Agent.Tests;

public class RenewalPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-400, 730 - 400, false)]   // 330 days left: far from the 90-day window
    [InlineData(-640, 730 - 640, true)]    // exactly 90 days left
    [InlineData(-700, 30, true)]
    [InlineData(-730, 0, true)]            // expiring now
    [InlineData(-729, -1, true)]           // already expired: still try, the old one may be accepted until it is refused
    public void Renews_inside_the_window(int notBeforeDays, int daysLeft, bool expected) =>
        Assert.Equal(expected, RenewalPolicy.ShouldRenew(Now.AddDays(notBeforeDays), Now.AddDays(daysLeft), Now, RenewalPolicy.DefaultWindow));

    [Fact]
    public void A_certificate_made_a_moment_ago_is_never_renewed_again_even_if_the_window_covers_its_whole_life()
    {
        // This is the loop seen in the manual run: a window longer than the certificate's life renewed on every connection.
        var made = Now.AddMinutes(-5);
        Assert.False(RenewalPolicy.ShouldRenew(made, made.AddDays(730), Now, TimeSpan.FromDays(900)));
        Assert.True(RenewalPolicy.ShouldRenew(made, made.AddDays(730), Now.AddDays(1).AddMinutes(1), TimeSpan.FromDays(900)));
    }
}
