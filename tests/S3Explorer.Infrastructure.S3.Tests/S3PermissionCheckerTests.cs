using System.Net;
using System.Reflection;
using Amazon.S3;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class S3PermissionCheckerTests
{
    [Fact]
    public async Task ReadCheckReportsHeadAndBoundedContentGetSeparately()
    {
        var (checker, storage) = CreateChecker();
        storage.HasObject = true;

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Read),
            TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCheckState.Passed, Assert.Single(result.Checks, value => value.Name == "ListBucket").State);
        var head = Assert.Single(result.Checks, value => value.Name == "HeadObject");
        Assert.Equal(PermissionCheckState.Passed, head.State);
        Assert.Contains("不会证明对象内容 GET", head.Message, StringComparison.Ordinal);
        var get = Assert.Single(result.Checks, value => value.Name == "GetObject");
        Assert.Equal(PermissionCheckState.Passed, get.State);
        Assert.Contains("小文件内容", get.Message, StringComparison.Ordinal);
        Assert.Contains("download", storage.Operations);
    }

    [Fact]
    public async Task EmptyPrefixLeavesHeadObjectIndeterminate()
    {
        var (checker, _) = CreateChecker();

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Read),
            TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCheckState.Indeterminate,
            Assert.Single(result.Checks, value => value.Name == "HeadObject").State);
    }

    [Fact]
    public async Task ReadCheckNeverDownloadsLargeObjectForPermissionVerification()
    {
        var (checker, storage) = CreateChecker();
        storage.HasObject = true;
        storage.ObjectSize = 10 * 1024 * 1024;

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Read),
            TestContext.Current.CancellationToken);

        var get = Assert.Single(result.Checks, value => value.Name == "GetObject");
        Assert.Equal(PermissionCheckState.Indeterminate, get.State);
        Assert.Contains("避免下载大文件", get.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("download", storage.Operations);
    }

    [Fact]
    public async Task ReadProbePassesHardTransportLimit()
    {
        var (checker, storage) = CreateChecker();
        storage.HasObject = true;

        await checker.CheckAsync(
            Request(StoragePermissionOperation.Read),
            TestContext.Current.CancellationToken);

        Assert.Equal(64 * 1024, storage.MaximumDownloadBytes);
    }

    [Fact]
    public async Task SafeCheckDoesNotRunWriteDeleteOrAclCalls()
    {
        var (checker, storage) = CreateChecker();

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Read | StoragePermissionOperation.Publish |
                    StoragePermissionOperation.Mirror | StoragePermissionOperation.PutObjectAcl),
            TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCheckState.Indeterminate, Assert.Single(result.Checks, value => value.Name == "PutObject").State);
        Assert.Equal(PermissionCheckState.Indeterminate, Assert.Single(result.Checks, value => value.Name == "DeleteObject").State);
        Assert.Equal(PermissionCheckState.Indeterminate, Assert.Single(result.Checks, value => value.Name == "PutObjectAcl").State);
        Assert.DoesNotContain(storage.Operations, value => value is "upload" or "delete" or "acl");
    }

    [Fact]
    public async Task ExplicitMutationProbeWritesSetsAclAndCleansUp()
    {
        var (checker, storage) = CreateChecker();

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Publish | StoragePermissionOperation.Mirror |
                StoragePermissionOperation.PutObjectAcl, allowMutation: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(["list", "upload", "acl", "delete"], storage.Operations);
        Assert.Equal(PermissionCheckState.Passed, Assert.Single(result.Checks, value => value.Name == "PutObject").State);
        Assert.Equal(PermissionCheckState.Passed, Assert.Single(result.Checks, value => value.Name == "PutObjectAcl").State);
        Assert.Equal(PermissionCheckState.Passed, Assert.Single(result.Checks, value => value.Name == "DeleteObject").State);
    }

    [Fact]
    public async Task CleanupFailureIsRequiredEvenForPublishProbe()
    {
        var (checker, storage) = CreateChecker();
        storage.FailDelete = true;

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Publish, allowMutation: true),
            TestContext.Current.CancellationToken);

        var cleanup = Assert.Single(result.Checks, value => value.Name == "DeleteObject");
        Assert.True(cleanup.Required);
        Assert.Equal(PermissionCheckState.Indeterminate, cleanup.State);
        Assert.Contains("可能仍保留", cleanup.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledUploadStillAttemptsCleanupWithIndependentToken()
    {
        var (checker, storage) = CreateChecker();
        storage.CancelUpload = true;

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Publish, allowMutation: true),
            TestContext.Current.CancellationToken);

        Assert.Contains("delete", storage.Operations);
        Assert.NotEqual(storage.UploadCancellationToken, storage.DeleteCancellationToken);
        var put = Assert.Single(result.Checks, value => value.Name == "PutObject");
        Assert.Equal(PermissionCheckState.Indeterminate, put.State);
        var delete = Assert.Single(result.Checks, value => value.Name == "DeleteObject");
        Assert.Equal(PermissionCheckState.Passed, delete.State);
    }

    [Fact]
    public async Task CancellationBeforeUploadDoesNotReportRemoteCleanup()
    {
        var (checker, storage) = CreateChecker();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Publish, allowMutation: true),
            cancellation.Token);

        Assert.DoesNotContain("upload", storage.Operations);
        Assert.DoesNotContain("delete", storage.Operations);
        Assert.Equal(
            PermissionCheckState.Indeterminate,
            Assert.Single(result.Checks, value => value.Name == "PutObject").State);
        Assert.DoesNotContain(result.Checks, value => value.Name == "DeleteObject");
    }

    [Fact]
    public async Task AccessDeniedKeepsSafeProviderDiagnostics()
    {
        var (checker, storage) = CreateChecker();
        storage.ListException = new AmazonS3Exception("secret details")
        {
            StatusCode = HttpStatusCode.Forbidden,
            ErrorCode = "AccessDenied",
            RequestId = "request-123"
        };

        var result = await checker.CheckAsync(
            Request(StoragePermissionOperation.Read),
            TestContext.Current.CancellationToken);

        var denied = Assert.Single(result.Checks, value => value.Name == "ListBucket");
        Assert.Equal(PermissionCheckState.Denied, denied.State);
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal("AccessDenied", denied.ProviderCode);
        Assert.Equal("request-123", denied.RequestId);
        Assert.DoesNotContain("secret details", denied.Message, StringComparison.Ordinal);
    }

    private static StoragePermissionCheckRequest Request(
        StoragePermissionOperation operation,
        bool allowMutation = false) => new(
        new ConnectionProfile
        {
            Name = "test",
            ServiceType = S3ServiceType.Custom,
            Endpoint = "https://s3.example.test",
            Region = "auto",
            AccessKey = "access",
            SecretKey = "secret"
        },
        "bucket",
        "deploy/",
        operation,
        allowMutation);

    private static (S3PermissionChecker Checker, StorageProxy Storage) CreateChecker()
    {
        var service = DispatchProxy.Create<IS3StorageService, StorageProxy>();
        return (new S3PermissionChecker(service), (StorageProxy)(object)service);
    }

    public class StorageProxy : DispatchProxy
    {
        public bool HasObject { get; set; }
        public long ObjectSize { get; set; } = 1;
        public bool FailDelete { get; set; }
        public bool CancelUpload { get; set; }
        public Exception? ListException { get; set; }
        public List<string> Operations { get; } = [];
        public long MaximumDownloadBytes { get; private set; }
        public CancellationToken DeleteCancellationToken { get; private set; }
        public CancellationToken UploadCancellationToken { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            return targetMethod.Name switch
            {
                nameof(IS3StorageService.ListObjectsAsync) => ListObjects(),
                nameof(IS3StorageService.GetObjectPropertiesAsync) => GetProperties(args),
                nameof(IS3StorageService.DownloadFileAsync) => Download(args),
                nameof(IS3StorageService.UploadFileAsync) => Upload(args),
                nameof(IS3StorageService.PutObjectAclAsync) => Record("acl"),
                nameof(IS3StorageService.DeleteObjectsAsync) => Delete(args),
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }

        private Task<PagedObjectResult> ListObjects()
        {
            Operations.Add("list");
            if (ListException is not null)
                return Task.FromException<PagedObjectResult>(ListException);
            IReadOnlyList<S3ObjectEntry> items = HasObject
                ? [new S3ObjectEntry("deploy/file.txt", "file.txt", ObjectSize, false, DateTimeOffset.UtcNow, "STANDARD")]
                : [];
            return Task.FromResult(new PagedObjectResult(items, null, false));
        }

        private static Task<ObjectProperties> GetProperties(object?[] args) => Task.FromResult(new ObjectProperties(
            (string)args[1]!,
            (string)args[2]!,
            1,
            DateTimeOffset.UtcNow,
            "etag",
            "text/plain",
            "STANDARD",
            null,
            new Dictionary<string, string>()));

        private Task Download(object?[] args)
        {
            Operations.Add("download");
            MaximumDownloadBytes = ((TransferOperationContext)args[4]!).Options.MaximumDownloadBytes;
            File.WriteAllBytes((string)args[3]!, [0x2A]);
            return Task.CompletedTask;
        }

        private Task Upload(object?[] args)
        {
            Operations.Add("upload");
            UploadCancellationToken = (CancellationToken)args[6]!;
            if (CancelUpload)
                return Task.FromException(new OperationCanceledException());
            return Task.CompletedTask;
        }

        private Task Record(string operation)
        {
            Operations.Add(operation);
            return Task.CompletedTask;
        }

        private Task Delete(object?[] args)
        {
            Operations.Add("delete");
            DeleteCancellationToken = (CancellationToken)args[3]!;
            return FailDelete
                ? Task.FromException(new InvalidOperationException("simulated cleanup failure"))
                : Task.CompletedTask;
        }
    }
}
