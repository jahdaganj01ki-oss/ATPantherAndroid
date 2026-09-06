using System.Security.Cryptography;
using System.Text;

namespace ATPanther.Windows;

/// <summary>
/// Port of CryptoExtensions.kt / PkceUtil.kt.
/// </summary>
internal static class CryptoUtils
{
    /// <summary>SHA-1 hex digest (lowercase), port of String.sha1().</summary>
    public static string Sha1Hex(string input)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>SHA-256 raw bytes, port of String.sha256Bytes().</summary>
    public static byte[] Sha256Bytes(string input)
        => SHA256.HashData(Encoding.UTF8.GetBytes(input));

    /// <summary>Base64url-encode without padding (RFC 7636), port of ByteArray.base64UrlNoPad().</summary>
    public static string Base64UrlNoPad(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Generate 32 random bytes, base64url-encoded, no padding → 43 chars.
    /// Port of randomCodeVerifier().
    /// </summary>
    public static string RandomCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlNoPad(bytes);
    }
}

/// <summary>Generate a PKCE (code_verifier, code_challenge) pair using S256. Port of PkcePair/generatePkce().</summary>
internal sealed record PkcePair(string CodeVerifier, string CodeChallenge)
{
    public static PkcePair Generate()
    {
        var verifier = CryptoUtils.RandomCodeVerifier();
        var challenge = CryptoUtils.Base64UrlNoPad(CryptoUtils.Sha256Bytes(verifier));
        return new PkcePair(verifier, challenge);
    }
}
