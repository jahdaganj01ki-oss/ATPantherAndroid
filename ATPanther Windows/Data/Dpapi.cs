using System.Runtime.InteropServices;
using System.Text;

namespace ATPanther.Data;

/// <summary>
/// Windows-DPAPI (CurrentUser-Scope) für das gespeicherte Passwort.
/// Bewusste Abweichung vom Android-Original (Klartext in SharedPreferences) –
/// Begründung und Folge in PARITY.md 6.4.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB dataIn, out string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x01;

    public static string Protect(string plainText)
    {
        byte[] input = Encoding.UTF8.GetBytes(plainText);
        IntPtr buffer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
        try
        {
            if (input.Length > 0) Marshal.Copy(input, 0, buffer, input.Length);
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = buffer };

            if (!CryptProtectData(ref inBlob, "AT Panther", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                  CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB outBlob))
            {
                throw new InvalidOperationException("DPAPI-Verschlüsselung nicht verfügbar");
            }

            try
            {
                byte[] output = new byte[outBlob.cbData];
                if (output.Length > 0) Marshal.Copy(outBlob.pbData, output, 0, output.Length);
                return Convert.ToBase64String(output);
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string Unprotect(string base64)
    {
        byte[] input = Convert.FromBase64String(base64);
        IntPtr buffer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
        try
        {
            if (input.Length > 0) Marshal.Copy(input, 0, buffer, input.Length);
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = buffer };

            if (!CryptUnprotectData(ref inBlob, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB outBlob))
            {
                throw new InvalidOperationException("DPAPI-Entschlüsselung fehlgeschlagen");
            }

            try
            {
                byte[] output = new byte[outBlob.cbData];
                if (output.Length > 0) Marshal.Copy(outBlob.pbData, output, 0, output.Length);
                return Encoding.UTF8.GetString(output);
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
