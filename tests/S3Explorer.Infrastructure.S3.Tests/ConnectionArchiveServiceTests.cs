using System.Text;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConnectionArchiveServiceTests
{
    private readonly ConnectionArchiveService _service = new();

    [Fact]
    public void CredentialFreeExportOmitsEveryCredentialValue()
    {
        var archive = _service.Export([CreateProfile()]);
        var json = Encoding.UTF8.GetString(archive);

        Assert.DoesNotContain("access-value", json);
        Assert.DoesNotContain("secret-value", json);
        Assert.DoesNotContain("session-value", json);
        var inspection = _service.Inspect(archive);
        Assert.False(inspection.ContainsCredentials);
        Assert.False(inspection.RequiresPassword);

        var imported = Assert.Single(_service.Import(archive).Profiles);
        Assert.Empty(imported.AccessKey);
        Assert.Empty(imported.SecretKey);
        Assert.Empty(imported.SessionToken);
        Assert.Equal("https://storage.example.test", imported.Endpoint);
    }

    [Fact]
    public void PasswordProtectedExportCanMoveReadyToUseCredentials()
    {
        var archive = _service.Export([CreateProfile()], includeCredentials: true, password: "portable-password");
        var json = Encoding.UTF8.GetString(archive);

        Assert.DoesNotContain("access-value", json);
        Assert.DoesNotContain("secret-value", json);
        Assert.DoesNotContain("session-value", json);
        Assert.True(_service.Inspect(archive).RequiresPassword);

        var imported = Assert.Single(_service.Import(archive, "portable-password").Profiles);
        Assert.Equal("access-value", imported.AccessKey);
        Assert.Equal("secret-value", imported.SecretKey);
        Assert.Equal("session-value", imported.SessionToken);
        imported.Validate();
    }

    [Fact]
    public void WrongPasswordIsReportedAsAuthenticationFailure()
    {
        var archive = _service.Export([CreateProfile()], includeCredentials: true, password: "portable-password");

        Assert.Throws<ConnectionArchiveAuthenticationException>(() =>
            _service.Import(archive, "wrong-password"));
    }

    [Fact]
    public void MergeRequiresExplicitCredentialChoice()
    {
        var imported = CreateProfile();

        var withoutCredentials = Assert.Single(_service.Merge(
            [], [imported], importCredentials: false, ConnectionImportConflictStrategy.Rename));
        var withCredentials = Assert.Single(_service.Merge(
            [], [imported], importCredentials: true, ConnectionImportConflictStrategy.Rename));

        Assert.Empty(withoutCredentials.AccessKey);
        Assert.Empty(withoutCredentials.SecretKey);
        Assert.Equal("secret-value", withCredentials.SecretKey);
    }

    [Theory]
    [InlineData(ConnectionImportConflictStrategy.Skip, 1, "Local")]
    [InlineData(ConnectionImportConflictStrategy.Replace, 1, "Remote")]
    [InlineData(ConnectionImportConflictStrategy.Rename, 2, "Local")]
    public void MergeHandlesDuplicateNames(
        ConnectionImportConflictStrategy strategy,
        int expectedCount,
        string expectedFirstAccessKey)
    {
        var existing = CreateProfile() with
        {
            Id = Guid.NewGuid(),
            Name = "Shared",
            AccessKey = "Local"
        };
        var imported = CreateProfile() with { Name = "shared", AccessKey = "Remote" };

        var result = _service.Merge([existing], [imported], importCredentials: true, strategy);

        Assert.Equal(expectedCount, result.Count);
        Assert.Equal(expectedFirstAccessKey, result[0].AccessKey);
        if (strategy == ConnectionImportConflictStrategy.Replace)
            Assert.Equal(existing.Id, result[0].Id);
        if (strategy == ConnectionImportConflictStrategy.Rename)
            Assert.Equal("shared (导入)", result[1].Name);
    }

    [Fact]
    public void TamperedCiphertextDoesNotImport()
    {
        var archive = _service.Export([CreateProfile()], includeCredentials: true, password: "portable-password");
        var text = Encoding.UTF8.GetString(archive);
        var payloadMarker = "\"encryptedPayload\": \"";
        var payloadStart = text.IndexOf(payloadMarker, StringComparison.Ordinal) + payloadMarker.Length;
        var changed = text[payloadStart] == 'A' ? 'B' : 'A';
        var tampered = Encoding.UTF8.GetBytes(text[..payloadStart] + changed + text[(payloadStart + 1)..]);

        Assert.Throws<ConnectionArchiveAuthenticationException>(() =>
            _service.Import(tampered, "portable-password"));
    }

    [Fact]
    public void ExternalCredentialExportKeepsSourceReferenceButNeverCopiesSecrets()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "AWS shared",
            CredentialSource = CredentialSourceKind.AwsSharedProfile,
            AwsProfileName = "audit",
            AccessKey = "stale-access",
            SecretKey = "stale-secret",
            SessionToken = "stale-session"
        };

        var archive = _service.Export([profile]);
        var text = Encoding.UTF8.GetString(archive);
        var imported = Assert.Single(_service.Import(archive).Profiles);

        Assert.Contains("\"version\": 2", text, StringComparison.Ordinal);
        Assert.Contains("audit", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-", text, StringComparison.Ordinal);
        Assert.Equal(CredentialSourceKind.AwsSharedProfile, imported.CredentialSource);
        Assert.Equal("audit", imported.AwsProfileName);
        Assert.Empty(imported.AccessKey);
        imported.Validate();
    }

    private static ConnectionProfile CreateProfile() => new()
    {
        Name = "Portable",
        ServiceType = S3ServiceType.Custom,
        Endpoint = "https://storage.example.test",
        Region = "auto",
        SignatureRegion = "us-east-1",
        AccessKey = "access-value",
        SecretKey = "secret-value",
        SessionToken = "session-value",
        DefaultBucket = "external-bucket",
        ExternalBuckets = ["other-bucket"]
    };
}
