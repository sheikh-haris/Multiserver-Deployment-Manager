using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace IisControlCenter.Agent.Tests;

public class CommandPolicyTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

    private AgentConfig Config(bool allowManaged = false, string env = "Dev") =>
        new() { PortalUrl = "https://agents.local", Environment = env, AllowedRoots = [@"d:\websites"], ServerId = Me, AllowManaged = allowManaged };

    private CommandEnvelope Cmd(Guid? server = null, string env = "Dev", TimeSpan? issuedOffset = null, TimeSpan? life = null, Guid? id = null, ServerMode mode = ServerMode.ReadOnly)
    {
        var issued = clock.GetUtcNow() + (issuedOffset ?? TimeSpan.Zero);
        return new CommandEnvelope(id ?? Guid.NewGuid(), server ?? Me, env, issued, issued + (life ?? TimeSpan.FromMinutes(5)), "c", mode);
    }

    [Fact]
    public void Accepts_a_valid_command() => Assert.Null(new CommandPolicy(Config(), clock).Validate(Cmd()));

    [Fact]
    public void Refuses_a_command_for_another_environment()
    {
        var reason = new CommandPolicy(Config(env: "Dev"), clock).Validate(Cmd(env: "Prod"));
        Assert.Contains("Prod", reason);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("DEV")]
    public void Environment_comparison_ignores_case(string env) => Assert.Null(new CommandPolicy(Config(), clock).Validate(Cmd(env: env)));

    [Fact] public void Refuses_a_command_for_another_server() =>
        Assert.NotNull(new CommandPolicy(Config(), clock).Validate(Cmd(server: Guid.NewGuid())));

    [Fact]
    public void Refuses_expired_commands()
    {
        var p = new CommandPolicy(Config(), clock);
        var c = Cmd();
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Contains("expired", p.Validate(c));
    }

    [Fact]
    public void Refuses_commands_from_the_future_but_allows_small_clock_skew()
    {
        var p = new CommandPolicy(Config(), clock);
        Assert.Null(p.Validate(Cmd(issuedOffset: TimeSpan.FromMinutes(1))));
        Assert.NotNull(p.Validate(Cmd(issuedOffset: TimeSpan.FromMinutes(10))));
    }

    [Fact]
    public void Refuses_commands_with_a_very_long_life() =>
        Assert.NotNull(new CommandPolicy(Config(), clock).Validate(Cmd(life: TimeSpan.FromHours(5))));

    [Fact]
    public void Refuses_a_replayed_command()
    {
        var p = new CommandPolicy(Config(), clock);
        var c = Cmd();
        Assert.Null(p.Validate(c));
        Assert.Equal("Duplicate command.", p.Validate(c));
    }

    [Fact]
    public void A_refused_command_is_not_remembered_as_seen_only_accepted_ones_are()
    {
        var p = new CommandPolicy(Config(), clock);
        var id = Guid.NewGuid();
        Assert.NotNull(p.Validate(Cmd(env: "Prod", id: id)));
        Assert.Null(p.Validate(Cmd(env: "Dev", id: id)));
    }

    [Fact]
    public void Remembers_a_bounded_number_of_commands()
    {
        var p = new CommandPolicy(Config(), clock);
        var first = Cmd();
        p.Validate(first);
        for (var i = 0; i < 1100; i++) p.Validate(Cmd());
        Assert.Null(p.Validate(Cmd(id: first.CommandId)));      // forgotten after 1000 newer ones (memory stays bounded)
    }

    [Theory]
    [InlineData(false, ServerMode.Managed, ServerMode.ReadOnly)]    // portal asks, local config says no
    [InlineData(true, ServerMode.Managed, ServerMode.Managed)]
    [InlineData(true, ServerMode.ReadOnly, ServerMode.ReadOnly)]
    [InlineData(false, ServerMode.ReadOnly, ServerMode.ReadOnly)]
    public void Local_config_has_the_last_word_on_managed_mode(bool allowed, ServerMode asked, ServerMode expected) =>
        Assert.Equal(expected, new CommandPolicy(Config(allowManaged: allowed), clock).EffectiveMode(asked));
}
