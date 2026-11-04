using System.Text;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioExactKeyIdentityIntegrationTests(ITestOutputHelper output)
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task UnsupportedMinioKeysAreRejectedWithoutReadingOrMutatingAliases()
    {
        var testToken = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var stage = "setup";
        Exception? operationFailure = null;
        void Stage(string value)
        {
            stage = value;
            output.WriteLine($"MinIO key-policy stage: {value}");
        }
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile()
            with { EnableMultipartCopy = true };
        var factory = new S3ClientFactory();
        using var client = factory.Create(profile);
        var service = new S3StorageService(factory);
        var bucket = $"s3explorer-key-policy-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "s3-key-policy-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new TransferOperationContext(new TransferExecutionOptions
        {
            RetainFailedTransferState = false
        }, new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
        const string alias = "identity/file";
        const string rootAlias = "file";
        const string validCopy = "copy/file";
        string[] cleanupKeys = [alias, rootAlias, validCopy];
        string[] unsupportedKeys = ["/identity/file", "identity//file", "/identity//file", "identity/./file", "identity/../file"];
        var aliasBytes = Encoding.UTF8.GetBytes("nested alias must retain these bytes");
        var rootBytes = Encoding.UTF8.GetBytes("root alias must also remain unchanged");
        var created = false;
        try
        {
            Stage("create isolated bucket");
            await service.CreateBucketAsync(profile, bucket, profile.Region, token);
            created = true;
            var aliasFile = Path.Combine(root, "alias-source.bin");
            var rootFile = Path.Combine(root, "root-source.bin");
            await File.WriteAllBytesAsync(aliasFile, aliasBytes, token);
            await File.WriteAllBytesAsync(rootFile, rootBytes, token);
            Stage("upload valid nested alias");
            await service.UploadFileAsync(profile, bucket, alias, aliasFile, "STANDARD", context, token);
            Stage("upload valid root alias");
            await service.UploadFileAsync(profile, bucket, rootAlias, rootFile, "STANDARD", context, token);

            // Pinned MinIO cannot represent these as independent keys: its HTTP
            // handlers clean a leading slash, and its validators reject // or dot
            // components. The application must refuse them before sending, never
            // pretend this backend supports every opaque AWS S3 key.
            foreach (var key in unsupportedKeys)
            {
                await RejectAsync($"upload unsupported key '{key}'", () =>
                    service.UploadFileAsync(profile, bucket, key, rootFile, "STANDARD", context, token));
                await RejectAsync($"HEAD unsupported key '{key}'", () =>
                    service.GetObjectPropertiesAsync(profile, bucket, key, token));
                await RejectAsync($"download unsupported key '{key}'", () =>
                    service.DownloadFileAsync(profile, bucket, key, Path.Combine(root, "rejected-download.bin"), context, token));
                await RejectAsync($"copy unsupported source '{key}'", () =>
                    service.CopyObjectAsync(profile, bucket, key, bucket, validCopy, context, token));
                await RejectAsync($"copy unsupported destination '{key}'", () =>
                    service.CopyObjectAsync(profile, bucket, rootAlias, bucket, key, context, token));
                await RejectAsync($"single delete unsupported key '{key}'", () =>
                    client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, token));
                await RejectAsync($"bulk delete selection containing unsupported key '{key}'", () =>
                    service.DeleteObjectsAsync(profile, bucket, [alias, key], token));
                await VerifyBytesAsync(alias, aliasBytes);
                await VerifyBytesAsync(rootAlias, rootBytes);
            }

            Stage("list unchanged exact aliases after all rejected operations");
            var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, token);
            Assert.Equal(new[] { alias, rootAlias }.Order(StringComparer.Ordinal),
                (listed.S3Objects ?? []).Select(item => item.Key).Order(StringComparer.Ordinal));
            Assert.False(File.Exists(Path.Combine(root, "rejected-download.bin")));
            Stage("create-only copy of representable key remains supported");
            var createOnly = context with { Options = context.Options with { AllowDestinationOverwrite = false } };
            await service.CopyObjectAsync(profile, bucket, alias, bucket, validCopy, createOnly, token);
            await VerifyBytesAsync(validCopy, aliasBytes);
            Stage("existing valid destination still reports create-only conflict");
            var conflict = await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.CopyObjectAsync(profile, bucket, rootAlias, bucket, validCopy, createOnly, token));
            Assert.Equal(TransferFailureCategory.Conflict, conflict.Failure.Category);
            await VerifyBytesAsync(validCopy, aliasBytes);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            exception.Data["MinioKeyPolicyStage"] = stage;
            output.WriteLine($"MinIO key-policy failed during '{stage}': {exception}");
            if (deadline.IsCancellationRequested && !testToken.IsCancellationRequested)
                throw new TimeoutException($"MinIO key-policy integration exceeded its two-minute deadline during '{stage}'.", exception);
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (created)
                {
                    // Never send impossible/normalized aliases to MinIO bulk delete,
                    // including during failure cleanup. Only real fixture keys belong here.
                    Stage("cleanup: delete representable isolated objects");
                    await service.DeleteObjectsAsync(profile, bucket, cleanupKeys, cleanup.Token);
                    Stage("cleanup: delete isolated bucket");
                    await service.DeleteEmptyBucketAsync(profile, bucket, cleanup.Token);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                output.WriteLine($"MinIO key-policy cleanup failed during '{stage}': {exception}");
                if (operationFailure is null) throw;
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception exception) when (operationFailure is not null || cleanupFailure is not null)
                {
                    output.WriteLine($"MinIO key-policy local cleanup also failed: {exception}");
                }
            }
        }

        async Task RejectAsync(string description, Func<Task> action)
        {
            Stage(description);
            var failure = await Assert.ThrowsAsync<TransferExecutionException>(action);
            Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
            Assert.False(failure.Failure.Retryable);
            Assert.Contains("MinIO", failure.Message, StringComparison.Ordinal);
        }

        async Task VerifyBytesAsync(string key, byte[] expected)
        {
            Stage($"HEAD exact valid object '{key}'");
            var properties = await service.GetObjectPropertiesAsync(profile, bucket, key, token);
            Assert.Equal(key, properties.Key);
            Assert.Equal(expected.LongLength, properties.Size);
            var destination = Path.Combine(root, $"download-{Guid.NewGuid():N}.bin");
            Stage($"download and verify exact valid object '{key}'");
            await service.DownloadFileAsync(profile, bucket, key, destination, context, token);
            Assert.Equal(expected, await File.ReadAllBytesAsync(destination, token));
        }
    }
}
