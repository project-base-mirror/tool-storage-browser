using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

[SupportedOSPlatform("windows")]
public sealed class DpapiCdnCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("S3Explorer.CdnCredentials.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        EnsureWindows();
        var data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Convert.ToBase64String(
                ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        EnsureWindows();
        var data = Convert.FromBase64String(ciphertext);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("CDN 凭据的 DPAPI 保护仅支持 Windows。");
    }
}
