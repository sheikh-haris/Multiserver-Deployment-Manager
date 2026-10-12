using IisControlCenter.Core.Audit;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;

namespace IisControlCenter.Core.Tests;

public class RegistrationTokenTests
{
    [Fact]
    public void Tokens_are_unique_and_long_and_url_safe()
    {
        var set = Enumerable.Range(0, 200).Select(_ => RegistrationTokens.Create().Token).ToHashSet();
        Assert.Equal(200, set.Count);
        Assert.All(set, t => { Assert.True(t.Length >= 43); Assert.DoesNotContain('+', t); Assert.DoesNotContain('/', t); Assert.DoesNotContain('=', t); });
    }

    [Fact]
    public void Hash_matches_and_is_not_the_token()
    {
        var (token, hash) = RegistrationTokens.Create();
        Assert.NotEqual(token, hash);
        Assert.True(RegistrationTokens.HashesEqual(hash, RegistrationTokens.Hash(token)));
        Assert.False(RegistrationTokens.HashesEqual(hash, RegistrationTokens.Hash(token + "x")));
    }
}

public class AuditHasherTests
{
    private static List<AuditEntry> Chain(int n)
    {
        var list = new List<AuditEntry>();
        var prev = "";
        for (var i = 1; i <= n; i++)
        {
            var e = new AuditEntry { Id = i, Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, i, TimeSpan.Zero), UserId = "u", UserName = "U", Action = "A" + i, PrevHash = prev, Result = "Success" };
            e.Hash = AuditHasher.Compute(e);
            prev = e.Hash;
            list.Add(e);
        }
        return list;
    }

    [Fact] public void Intact_chain_has_no_break() => Assert.Null(AuditHasher.FindBreak(Chain(5)));

    [Fact]
    public void Edited_row_is_detected()
    {
        var c = Chain(5);
        c[2].After = "changed";
        Assert.Equal(3, AuditHasher.FindBreak(c));
    }

    [Fact]
    public void Deleted_row_is_detected()
    {
        var c = Chain(5);
        c.RemoveAt(2);
        Assert.Equal(4, AuditHasher.FindBreak(c));
    }

    [Fact]
    public void Hash_depends_on_every_field()
    {
        var a = Chain(1)[0];
        var h = a.Hash;
        a.Detail = "x";
        Assert.NotEqual(h, AuditHasher.Compute(a));
    }
}
