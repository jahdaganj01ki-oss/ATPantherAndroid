using System.Security.Cryptography;
using System.Text;

namespace ATPanther.AldiTalk;

/// <summary>
/// PKCE (RFC 7636) mit S256 – Port von PkceUtil.kt der Android-Version.
/// </summary>
public static class Pkce
{
    public static (string Verifier, string Challenge) Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes); // 43 Zeichen, kein Padding
        var challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        var challenge = Base64UrlEncode(challengeBytes);
        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
