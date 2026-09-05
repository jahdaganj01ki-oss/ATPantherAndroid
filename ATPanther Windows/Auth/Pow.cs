using System.Security.Cryptography;
using System.Text;

namespace ATPanther.Auth;

/// <summary>
/// SHA-1-Proof-of-Work wie <c>AuthService.kt:43–49</c>: kleinstes nonce in
/// <c>0..10_000_000</c> (inklusive), dessen <c>sha1hex(work + nonce)</c> mit
/// <c>difficulty</c> Nullen beginnt.
///
/// Implementierung ohne String-Allokationen pro Versuch – auf einem Handy laufen
/// hier bis zu 10 Mio. Hashes, auf Windows genauso. Die Hex-Prüfung erfolgt direkt
/// auf den Bytes (führende "0"-Zeichen == führende Null-Nibbles).
/// </summary>
public static class Pow
{
    public const int MaxNonce = 10_000_000;

    public static int Solve(string work, int difficulty = AuthConfig.POW_DIFFICULTY)
    {
        if (difficulty < 0) throw new InvalidOperationException("PoW: negative difficulty");
        if (difficulty > 40) throw new InvalidOperationException("PoW nicht gelöst (10M Versuche)");

        int fullZeroBytes = difficulty / 2;
        bool checkHighNibble = difficulty % 2 == 1;

        Span<byte> buffer = stackalloc byte[128];
        Span<byte> hash = stackalloc byte[20];

        int workLen = Encoding.UTF8.GetBytes(work, buffer);
        if (workLen + 11 > buffer.Length) throw new InvalidOperationException("PoW: work zu lang");

        for (int nonce = 0; nonce <= MaxNonce; nonce++)
        {
            int length = workLen + WriteDecimal(buffer.Slice(workLen), nonce);
            SHA1.HashData(buffer.Slice(0, length), hash);

            bool matches = true;
            for (int i = 0; i < fullZeroBytes; i++)
            {
                if (hash[i] != 0) { matches = false; break; }
            }
            if (matches && checkHighNibble && (hash[fullZeroBytes] & 0xF0) != 0) matches = false;
            if (matches) return nonce;
        }

        throw new InvalidOperationException("PoW nicht gelöst (10M Versuche)");
    }

    /// <summary>Schreibt die Dezimaldarstellung von <paramref name="value"/> (ASCII) in den Puffer.</summary>
    private static int WriteDecimal(Span<byte> target, int value)
    {
        if (value == 0)
        {
            target[0] = (byte)'0';
            return 1;
        }

        Span<byte> digits = stackalloc byte[11];
        int count = 0;
        int rest = value;
        while (rest > 0)
        {
            digits[count++] = (byte)('0' + rest % 10);
            rest /= 10;
        }
        for (int i = 0; i < count; i++) target[i] = digits[count - 1 - i];
        return count;
    }
}
