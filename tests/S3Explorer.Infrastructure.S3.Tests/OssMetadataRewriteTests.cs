using System.Reflection;
using Aliyun.OSS;
using S3Explorer.Core;
using Xunit;
using OssMetadata = Aliyun.OSS.ObjectMetadata;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class OssMetadataRewriteTests
{
    private static ObjectProperties Expected => new("bucket", "path/key", 42, null, "\"etag\"", "text/plain", "STANDARD", null,
        new Dictionary<string, string> { ["untouched"] = " value " }) { ProviderObjectType = "Normal" };
    private static ObjectWriteHeaders Edits => new("application/json", "max-age=60", Metadata: new Dictionary<string, string> { ["changed"] = " keep spaces " });
    private static OssMetadata Head(string contentType = "text/plain")
    {
        var value = new OssMetadata { ContentLength = 42, ETag = "etag", ContentType = contentType };
        value.AddHeader("x-oss-object-type", "Normal");
        value.AddHeader("x-oss-storage-class", "Standard");
        value.UserMetadata.Add("untouched", " value ");
        return value;
    }
    private static void Save(Client client, CancellationToken? token = null) =>
        S3StorageService.RewriteOssMetadata(client, Expected, Edits, token ?? TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(CannedAccessControlList.Default, "default")]
    [InlineData(CannedAccessControlList.Private, "private")]
    [InlineData(CannedAccessControlList.PublicRead, "public-read")]
    [InlineData(CannedAccessControlList.PublicReadWrite, "public-read-write")]
    public void NativeWirePreservesAclStorageTagsAndCondition(CannedAccessControlList acl, string wireAcl)
    {
        var source = Head();
        source.ServerSideEncryption = "AES256";
        source.AddHeader("Content-Language", "zh-CN");
        var request = S3StorageService.BuildOssMetadataRequest(Expected, source, acl, Edits.ValidateForMetadataCopy());
        var wire = new Dictionary<string, string>();
        typeof(CopyObjectRequest).GetMethod("Populate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(request, [wire]);
        Assert.Equal("/bucket/path/key", wire["x-oss-copy-source"]);
        Assert.Equal("etag", wire["x-oss-copy-source-if-match"].Trim('"'));
        Assert.Equal("REPLACE", wire["x-oss-metadata-directive"]);
        Assert.Equal(wireAcl, wire["x-oss-object-acl"]);
        Assert.Equal("Standard", wire["x-oss-storage-class"]);
        Assert.Equal("AES256", wire["x-oss-server-side-encryption"]);
        Assert.Equal("COPY", wire["x-oss-tagging-directive"]);
        Assert.Equal("zh-CN", wire["Content-Language"]);
        Assert.Equal(" keep spaces ", wire["x-oss-meta-changed"]);
        Assert.Null(request.SourceVersionId);
        Assert.Equal(request.SourceKey, request.DestinationKey);
        Assert.DoesNotContain("Content-Length", wire.Keys);
    }

    [Fact]
    public void OrdinaryUnversionedObjectUsesOneCopyAndTwoChecks()
    {
        var client = new Client();
        Save(client);
        Assert.Equal(1, client.CopyCalls);
        Assert.Equal(2, client.HeadCalls);
        Assert.Equal(2, client.VersionCalls);
        Assert.Equal(2, client.LockCalls);
    }

    [Theory]
    [InlineData(VersioningStatus.Enabled)]
    [InlineData(VersioningStatus.Suspended)]
    public void VersionedBucketCannotUseMetadataOnlyPath(VersioningStatus status)
    {
        var client = new Client { Status = status };
        Assert.Throws<NotSupportedException>(() => Save(client));
        Assert.Equal(0, client.CopyCalls);
    }

    [Fact]
    public void VersioningEnabledDuringPreflightStopsCopy()
    {
        var client = new Client { ChangeVersion = true };
        Assert.Throws<NotSupportedException>(() => Save(client));
        Assert.Equal(0, client.CopyCalls);
    }

    [Fact]
    public void SameEtagMetadataOrAclChangeStopsCopy()
    {
        var metadataRace = new Client { ChangeHeader = true };
        Assert.Throws<InvalidOperationException>(() => Save(metadataRace));
        Assert.Equal(0, metadataRace.CopyCalls);
        var aclRace = new Client { ChangeAcl = true };
        Assert.Throws<InvalidOperationException>(() => Save(aclRace));
        Assert.Equal(0, aclRace.CopyCalls);
    }

    [Fact]
    public void LockQueryFailureIsNeverTreatedAsNoProtection()
    {
        var client = new Client { FailLock = true };
        Assert.Throws<UnauthorizedAccessException>(() => Save(client));
        Assert.Equal(0, client.CopyCalls);
    }

    [Theory]
    [InlineData("Appendable")]
    [InlineData("Symlink")]
    [InlineData("")]
    public void NonNormalObjectDoesNotChangeType(string type)
    {
        var client = new Client { ObjectType = type };
        Assert.Throws<NotSupportedException>(() => Save(client));
        Assert.Equal(0, client.CopyCalls);
    }

    [Fact]
    public void UncertainCopyDoesNotRetry()
    {
        var client = new Client { FailCopy = true };
        var error = Assert.Throws<InvalidOperationException>(() => Save(client));
        Assert.Contains("未确认", error.Message);
        Assert.Equal(1, client.CopyCalls);
    }

    [Theory]
    [InlineData("https://s3.oss-cn-hangzhou.aliyuncs.com", "https://oss-cn-hangzhou.aliyuncs.com", "cn-hangzhou")]
    [InlineData("https://oss-cn-shanghai-internal.aliyuncs.com", "https://oss-cn-shanghai-internal.aliyuncs.com", "cn-shanghai")]
    public void EndpointConversionOnlyUsesOfficialSelectedRegion(string input, string endpoint, string region)
    {
        var actual = S3StorageService.GetOssNativeEndpoint(ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with { Endpoint = input, Region = region, SignatureRegion = region });
        Assert.Equal((endpoint, region), actual);
    }

    [Theory]
    [InlineData("https://storage.example.com")]
    [InlineData("http://oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("https://oss-accelerate.aliyuncs.com")]
    [InlineData("https://oss-cn-hangzhou.aliyuncs.com/proxy")]
    public void UnsupportedNativeRoutingFailsBeforeCredentialsAreSent(string endpoint) =>
        Assert.Throws<NotSupportedException>(() => S3StorageService.GetOssNativeEndpoint(
            ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with { Endpoint = endpoint }));

    [Fact]
    public void NativeEndpointCannotSilentlyOverrideSigningRegion()
    {
        Assert.Throws<NotSupportedException>(() => S3StorageService.GetOssNativeEndpoint(
            ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with { SignatureRegion = "cn-shanghai" }));
        Assert.Equal("cn-hangzhou", S3StorageService.GetOssNativeEndpoint(
            ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss)).Region);
    }

    [Theory]
    [InlineData("Standard", "STANDARD", true)]
    [InlineData("Standard", "Standard", true)]
    [InlineData("IA", "STANDARD_IA", true)]
    [InlineData("IA", "IA", true)]
    [InlineData("Archive", "GLACIER", false)]
    [InlineData("Standard", "UNKNOWN", false)]
    public void StorageClassOnlyUsesKnownNativeAliases(string native, string snapshot, bool matches)
    {
        var head = Head();
        head.HttpMetadata["x-oss-storage-class"] = native;
        Assert.Equal(matches, S3StorageService.OssMatchesExpected(head, Expected with { StorageClass = snapshot }));
    }

    [Fact]
    public void SnapshotProtectionAndNativeHeaderChangesAreConflicts()
    {
        ObjectProperties[] changed = [
            Expected with { ServerSideEncryptionMethod = "AES256" },
            Expected with { EncryptionKeyId = "key" },
            Expected with { CustomerEncryptionMethod = "AES256" },
            Expected with { VersionId = "old-version" },
            Expected with { WebsiteRedirectLocation = "/new" },
            Expected with { ProviderObjectType = "Appendable" },
            Expected with { ProviderObjectType = null },
            Expected with { ProviderRetentionUntil = "future" },
            Expected with { ObjectLockMode = "COMPLIANCE" },
            Expected with { MissingMetadataCount = 1 }
        ];
        foreach (var snapshot in changed) Assert.False(S3StorageService.OssMatchesExpected(Head(), snapshot));
    }

    private sealed class Client : S3StorageService.IOssMetadataClient
    {
        public VersioningStatus Status { get; init; } = VersioningStatus.Off;
        public bool ChangeVersion { get; init; }
        public bool ChangeHeader { get; init; }
        public bool ChangeAcl { get; init; }
        public bool FailLock { get; init; }
        public bool FailCopy { get; init; }
        public string ObjectType { get; init; } = "Normal";
        public int CopyCalls { get; private set; }
        public int VersionCalls { get; private set; }
        public int HeadCalls { get; private set; }
        public int LockCalls { get; private set; }
        private int _aclCalls;
        public VersioningStatus Versioning(string bucket) => ++VersionCalls > 1 && ChangeVersion ? VersioningStatus.Enabled : Status;
        public void RequireNoBucketWorm(string bucket)
        {
            LockCalls++;
            if (FailLock) throw new UnauthorizedAccessException();
        }
        public OssMetadata Head(string bucket, string key)
        {
            var value = OssMetadataRewriteTests.Head(++HeadCalls > 1 && ChangeHeader ? "application/changed" : "text/plain");
            value.HttpMetadata["x-oss-object-type"] = ObjectType;
            return value;
        }
        public CannedAccessControlList Acl(string bucket, string key) => ++_aclCalls > 1 && ChangeAcl ? CannedAccessControlList.PublicRead : CannedAccessControlList.Default;
        public void Copy(CopyObjectRequest request)
        {
            CopyCalls++;
            if (FailCopy) throw new TimeoutException();
        }
    }
}
