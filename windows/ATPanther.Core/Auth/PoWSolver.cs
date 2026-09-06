using System.Security.Cryptography;
using System.Text;

namespace ATPanther.Core.Auth;

/// <summary>
/// 1:1 port of the Android <c>solvePow</c> function in AuthService.kt:
/// find a nonce where sha1(workUuid + nonce) starts with "0" repeated difficulty times.
/// </summary>
public static class PoWSolver
{
    public static string Solve(string workUuid, int difficulty)
    {
        var target = new string('0', difficulty);
        for (var nonce = 0; nonce <= AuthConfig.MaxPowNonce; nonce++)
        {
            if (Sha1Hex(workUuid + nonce).StartsWith(target, StringComparison.Ordinal))
            {
                return nonce.ToString();
            }
        }

        throw new InvalidOperationException("PoW nicht gelöst (10M Versuche)");
    }

    private static string Sha1Hex(string input)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
