using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ATPanther;

/// <summary>
/// Verschlüsselte Speicherung der ALDI-Talk-Zugangsdaten über Windows DPAPI
/// (DataProtectionScope.CurrentUser) – das Windows-Pendant zu
/// EncryptedSharedPreferences der Android-Version. Die Datei kann nur vom
/// angemeldeten Windows-Benutzer entschlüsselt werden.
/// </summary>
public static class SecureStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ATPanther.Credentials.v1");

    public static void Save(string phone, string password)
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.SerializeToUtf8Bytes(new { phone, password });
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.CredentialsFile, encrypted);
    }

    public static (string Phone, string Password)? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.CredentialsFile)) return null;
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(AppPaths.CredentialsFile),
                Entropy,
                DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(decrypted);
            var phone = doc.RootElement.GetProperty("phone").GetString() ?? "";
            var password = doc.RootElement.GetProperty("password").GetString() ?? "";
            return (phone, password);
        }
        catch
        {
            // Nicht entschlüsselbar (fremder User / korrupt) → als nicht vorhanden behandeln
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(AppPaths.CredentialsFile)) File.Delete(AppPaths.CredentialsFile);
        }
        catch
        {
            // ignore
        }
    }
}
