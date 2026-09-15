using System.Security.Cryptography;
using System.Text;

namespace ATPanther.Core;

public static class Crypto
{
    public static string Sha1Hex(string input)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static byte[] Sha256Bytes(string input) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(input));

    public static string Base64UrlNoPad(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string RandomCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlNoPad(bytes); // 43 Zeichen
    }

    public static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = RandomCodeVerifier();
        var challenge = Base64UrlNoPad(Sha256Bytes(verifier));
        return (verifier, challenge);
    }

    /// <summary>SHA-1 PoW: finde Nonce, sodass SHA1(workUuid+nonce) mit "0"*difficulty beginnt.</summary>
    public static int SolvePow(string workUuid, int difficulty, CancellationToken ct = default)
    {
        var target = new string('0', difficulty);
        for (var nonce = 0; nonce <= 10_000_000; nonce++)
        {
            if ((nonce & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (Sha1Hex(workUuid + nonce).StartsWith(target, StringComparison.Ordinal))
                return nonce;
        }
        throw new InvalidOperationException("PoW nicht geloest (10M Versuche)");
    }

    public static Task<int> SolvePowAsync(string workUuid, int difficulty, CancellationToken ct = default) =>
        Task.Run(() => SolvePow(workUuid, difficulty, ct), ct);
}
