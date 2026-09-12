using System.Security.Cryptography;
using System.Text;

namespace ATPanther.Windows;

public static class Credentials
{
    private const string CredentialPrefix = "ATPanther:";

    public static string Store(string key, string plaintext)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            null,
            DataProtectionScope.CurrentUser);

        var protectedText = Convert.ToBase64String(protectedBytes);

        var folder = GetCredentialFolder();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{CredentialPrefix}{key}.bin");
        File.WriteAllText(path, protectedText, Encoding.UTF8);
        return path;
    }

    public static string? Retrieve(string key)
    {
        var folder = GetCredentialFolder();
        var path = Path.Combine(folder, $"{CredentialPrefix}{key}.bin");
        if (!File.Exists(path)) return null;

        var protectedText = File.ReadAllText(path, Encoding.UTF8);
        var protectedBytes = Convert.FromBase64String(protectedText);
        var plaintextBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plaintextBytes);
    }

    public static bool Exists(string key)
    {
        var folder = GetCredentialFolder();
        var path = Path.Combine(folder, $"{CredentialPrefix}{key}.bin");
        return File.Exists(path);
    }

    public static void Remove(string key)
    {
        var folder = GetCredentialFolder();
        var path = Path.Combine(folder, $"{CredentialPrefix}{key}.bin");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetCredentialFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ATPanther",
            "credentials");
    }
}
