using System.Collections.Concurrent;
using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class UploadPathTests
{
    [Theory]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Auto, false)]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Auto, true)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.UnsignedPayload, false)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.UnsignedPayload, true)]
    public async Task SingleAndDirectoryUploadKeepSigningModeAndVerifyEveryFile(S3ServiceType type, UploadSigningMode mode, bool directory)
    {
        var root = Path.Combine(Path.GetTempPath(), "s3-cli-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var first = Path.Combine(root, "single.txt");
            await File.WriteAllTextAsync(first, "isolated first", token);
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "二 + %.txt"), "isolated second", token);
            var profile = ConnectionProfile.CreatePreset(type) with
            {
                UploadSigningMode = mode, Name = "r2-test", Endpoint = "https://storage.example.test", AccessKey = "fake-access", SecretKey = "fake-secret"
            };
            var service = DispatchProxy.Create<IS3StorageService, UploadStorage>();
            var recorder = (UploadStorage)(object)service;
            recorder.ExpectedProfile = profile;
            var transfer = CliTransferRuntime.Create(CliArguments.Parse(["upload", "--transfers", "2"]));
            var method = typeof(Program).GetMethod("UploadPathAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var result = await Assert.IsAssignableFrom<Task<int>>(method.Invoke(null,
                [service, profile, directory ? root : first, "bucket", "prefix/", true, transfer, token]));
            Assert.Equal(directory ? 2 : 1, result);
            Assert.Equal(result, recorder.Objects.Count);
            Assert.Equal(result, recorder.Verified.Count);
            Assert.All(recorder.Objects.Keys, key => Assert.StartsWith("prefix/", key));
            if (directory) Assert.Contains(recorder.Objects.Keys, key => key.EndsWith("nested/二 + %.txt", StringComparison.Ordinal));
            else Assert.Contains("prefix/single.txt", recorder.Objects.Keys);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public class UploadStorage : DispatchProxy
    {
        public ConnectionProfile ExpectedProfile = null!;
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public ConcurrentBag<string> Verified { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(args);
            Assert.Same(ExpectedProfile, args[0]);
            Assert.Equal("bucket", args[1]);
            var key = (string)args[2]!;
            var path = (string)args[3]!;
            var token = (CancellationToken)args[^1]!;
            return targetMethod!.Name switch
            {
                nameof(IS3StorageService.UploadFileAsync) => UploadAsync(key, path, token),
                nameof(IS3StorageService.DownloadFileAsync) => DownloadAsync(key, path, token),
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }
        private async Task UploadAsync(string key, string path, CancellationToken token) => Objects[key] = await File.ReadAllBytesAsync(path, token);
        private async Task DownloadAsync(string key, string path, CancellationToken token)
        {
            await File.WriteAllBytesAsync(path, Objects[key], token);
            Verified.Add(key);
        }
    }
}
