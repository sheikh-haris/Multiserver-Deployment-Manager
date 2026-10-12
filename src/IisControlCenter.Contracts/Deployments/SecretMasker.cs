using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IisControlCenter.Contracts.Deployments;

/// <summary>
/// Hides secret-looking values in config text before it is shown or sent.
/// It is pattern based, so it can miss a secret. The UI says so. Both sides of a diff are masked with the same salt,
/// so "same secret" and "changed secret" stay visible without showing the secret.
/// </summary>
public static class SecretMasker
{
    private const string Dots = "••••";
    private static readonly string SecretWords = @"pass(?:word|wd|phrase)?|pwd|secret|token|api[-_ ]?key|apikey|private[-_ ]?key|connection[-_ ]?string|credential|signature|bearer|validationkey|decryptionkey|client[-_ ]?secret|access[-_ ]?key|accountkey|sharedaccesskey";

    // key="value" or key='value' in XML where the KEY NAME looks secret (connectionString, password, ...)
    private static readonly Regex XmlAttr = new($@"(?<k>\b[\w.\-]*(?:{SecretWords})[\w.\-]*)(?<eq>\s*=\s*)(?<q>[""'])(?<v>.*?)\k<q>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    // <add key="SomeSecret" value="..."/> and <setting name="Password"><value>...</value>
    private static readonly Regex AddKeyValue = new($@"(?<pre><add\s+(?:key|name)\s*=\s*[""'][^""']*(?:{SecretWords})[^""']*[""']\s+value\s*=\s*)(?<q>[""'])(?<v>.*?)\k<q>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex XmlElement = new($@"(?<pre><[\w.\-]*(?:{SecretWords})[\w.\-]*[^>]*>)(?<v>[^<]+)(?<post></)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // JSON: "Password": "value"
    private static readonly Regex JsonString = new($@"(?<pre>""[^""]*(?:{SecretWords})[^""]*""\s*:\s*)""(?<v>(?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // inside a connection string: Password=xxx; Pwd=xxx; Account Key=xxx
    private static readonly Regex ConnPart = new($@"(?<pre>\b(?:{SecretWords}|account\s*key)\s*=\s*)(?<v>[^;""'<>\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // long opaque strings (base64 or hex) of 32+ chars inside quotes or after '='
    private static readonly Regex LongOpaque = new(@"(?<pre>[=:]\s*[""']?)(?<v>[A-Za-z0-9+/=_\-]{32,})(?<post>[""']?)", RegexOptions.Compiled);

    public static string Mask(string text, string salt = "")
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;

        s = AddKeyValue.Replace(s, m => $"{m.Groups["pre"].Value}{m.Groups["q"].Value}{Token(m.Groups["v"].Value, salt)}{m.Groups["q"].Value}");
        s = XmlAttr.Replace(s, m => $"{m.Groups["k"].Value}{m.Groups["eq"].Value}{m.Groups["q"].Value}{MaskConnectionOrValue(m.Groups["v"].Value, salt)}{m.Groups["q"].Value}");
        s = XmlElement.Replace(s, m => $"{m.Groups["pre"].Value}{Token(m.Groups["v"].Value.Trim(), salt)}{m.Groups["post"].Value}");
        s = JsonString.Replace(s, m => $"{m.Groups["pre"].Value}\"{MaskConnectionOrValue(m.Groups["v"].Value, salt)}\"");
        // Connection strings stored under an innocent key name, for example "Default": "Server=a;Password=b".
        s = ConnPart.Replace(s, m => IsAlreadyMasked(m.Groups["v"].Value) ? m.Value : $"{m.Groups["pre"].Value}{Token(m.Groups["v"].Value, salt)}");
        s = LongOpaque.Replace(s, m => IsAlreadyMasked(m.Groups["v"].Value) ? m.Value : $"{m.Groups["pre"].Value}{Token(m.Groups["v"].Value, salt)}{m.Groups["post"].Value}");
        return s;
    }

    private static bool IsAlreadyMasked(string v) => v.Contains(Dots);

    /// <summary>If the value is itself a connection string, hide only its secret parts; otherwise hide it all.</summary>
    private static string MaskConnectionOrValue(string v, string salt)
    {
        if (IsAlreadyMasked(v)) return v;
        if (v.Contains(';') && v.Contains('='))
            return ConnPart.Replace(v, m => $"{m.Groups["pre"].Value}{Token(m.Groups["v"].Value, salt)}");
        return Token(v, salt);
    }

    /// <summary>"••••[ab12]" The 4 hex characters let a reader see whether a secret changed without revealing it.</summary>
    public static string Token(string value, string salt)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + value));
        return $"{Dots}[{Convert.ToHexString(h, 0, 2).ToLowerInvariant()}]";
    }
}
