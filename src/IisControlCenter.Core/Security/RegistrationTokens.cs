using System.Security.Cryptography;
using System.Text;

namespace IisControlCenter.Core.Security;

public static class RegistrationTokens
{
    /// <summary>Returns a new random token (shown once) and its hash (stored).</summary>
    public static (string Token, string Hash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool HashesEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
