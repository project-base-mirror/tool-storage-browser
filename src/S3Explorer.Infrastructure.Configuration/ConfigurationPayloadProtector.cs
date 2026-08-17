using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace S3Explorer.Infrastructure.Configuration;

public interface IConfigurationPayloadProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiConfigurationPayloadProtector : IConfigurationPayloadProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("S3Explorer.Configuration.v1");

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        EnsureWindows();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        EnsureWindows();
        var protectedBytes = Convert.FromBase64String(ciphertext);
        byte[]? bytes = null;
        try { bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser); return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("统一配置的 DPAPI 保护仅支持 Windows。");
    }
}
