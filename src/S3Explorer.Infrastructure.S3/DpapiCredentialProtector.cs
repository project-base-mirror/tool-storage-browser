using System.Security.Cryptography;
using System.Text;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed class DpapiCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("S3Explorer.ProfileCredentials.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅在 Windows 上可用。");

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(bytes);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
            return string.Empty;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅在 Windows 上可用。");

        var protectedBytes = Convert.FromBase64String(ciphertext);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
