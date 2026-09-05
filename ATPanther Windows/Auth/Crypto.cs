using System.Security.Cryptography;
using System.Text;

namespace ATPanther.Auth;

/// <summary>
/// Hash- und Base64Url-Helfer. Pendant zu <c>util/CryptoExtensions.kt</c> und
/// <c>util/PkceUtil.kt</c> (API-CONTRACT 2.3, 2.5).
/// </summary>
public static class Crypto
{
    /// <summary>
    /// Kotlin: <c>MessageDigest.getInstance("SHA-1").digest(toByteArray())</c> mit
    /// <c>"%02x"</c>-Ausgabe ⇒ UTF-8-Bytes, Kleinbuchstaben-Hex (CryptoExtensions.kt:8–11, 20).
    /// </summary>
    public static string Sha1Hex(string input)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>SHA-256 als Rohbytes (CryptoExtensions.kt:14–17).</summary>
    public static byte[] Sha256Bytes(string input) => SHA256.HashData(Encoding.UTF8.GetBytes(input));

    /// <summary>
    /// Base64Url ohne Padding – wie <c>Base64.getUrlEncoder().withoutPadding()</c>
    /// (CryptoExtensions.kt:23–24).
    /// </summary>
    public static string Base64UrlNoPad(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>32 Zufallsbytes, base64url, ohne Padding ⇒ 43 Zeichen (CryptoExtensions.kt:27–28).</summary>
    public static string RandomCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlNoPad(bytes);
    }
}

/// <summary>PKCE-Paar (PkceUtil.kt:4–10).</summary>
public sealed record PkcePair(string CodeVerifier, string CodeChallenge);

public static class Pkce
{
    public static PkcePair Generate()
    {
        string verifier = Crypto.RandomCodeVerifier();
        string challenge = Crypto.Base64UrlNoPad(Crypto.Sha256Bytes(verifier));
        return new PkcePair(verifier, challenge);
    }
}
