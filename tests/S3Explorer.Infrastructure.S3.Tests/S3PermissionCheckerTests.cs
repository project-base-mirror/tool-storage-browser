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
    public async Task ReadCheckReportsListAndHeadWithoutClaimingContentGet()
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
        Assert.DoesNotContain(result.Checks, value => value.Name == "GetObject");
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
        public bool FailDelete { get; set; }
        public Exception? ListException { get; set; }
        public List<string> Operations { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            return targetMethod.Name switch
            {
                nameof(IS3StorageService.ListObjectsAsync) => ListObjects(),
                nameof(IS3StorageService.GetObjectPropertiesAsync) => GetProperties(args),
                nameof(IS3StorageService.UploadFileAsync) => Record("upload"),
                nameof(IS3StorageService.PutObjectAclAsync) => Record("acl"),
                nameof(IS3StorageService.DeleteObjectsAsync) => Delete(),
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }

        private Task<PagedObjectResult> ListObjects()
        {
            Operations.Add("list");
            if (ListException is not null)
                return Task.FromException<PagedObjectResult>(ListException);
            IReadOnlyList<S3ObjectEntry> items = HasObject
                ? [new S3ObjectEntry("deploy/file.txt", "file.txt", 1, false, DateTimeOffset.UtcNow, "STANDARD")]
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

        private Task Record(string operation)
        {
            Operations.Add(operation);
            return Task.CompletedTask;
        }

        private Task Delete()
        {
            Operations.Add("delete");
            return FailDelete
                ? Task.FromException(new InvalidOperationException("simulated cleanup failure"))
                : Task.CompletedTask;
        }
    }
}
