using System.Security.Cryptography;
using System.Text;

namespace ATPanther.Core.Auth;

public sealed record PkcePair(string CodeVerifier, string CodeChallenge)
{
    public static PkcePair Generate()
    {
        var verifier = RandomCodeVerifier();
        var challenge = Base64UrlNoPad(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }

    private static string RandomCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Base64UrlNoPad(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
