using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using S3Explorer.Infrastructure.Configuration;
using Xunit;

namespace S3Explorer.Infrastructure.Configuration.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationProcessLockTests
{
    [Fact]
    public async Task WaitingOpenRecoversAfterLockHolderIsKilled()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "S3Explorer.LockTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ready = Path.Combine(root, "holder-ready");
        var lockPath = Path.Combine(root, "configuration.lock");
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            var script = $"$lock = [IO.File]::Open('{lockPath.Replace("'", "''")}', 'OpenOrCreate', 'ReadWrite', 'None'); " +
                $"[IO.File]::WriteAllText('{ready.Replace("'", "''")}', 'ready'); Start-Sleep -Seconds 60";
            info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                info.ArgumentList.Add(argument);
        }
        else
        {
            info = new ProcessStartInfo("python3") { UseShellExecute = false };
            foreach (var argument in new[]
            {
                "-c", "import fcntl,pathlib,sys,time; f=open(sys.argv[1],'a'); fcntl.flock(f,fcntl.LOCK_EX); pathlib.Path(sys.argv[2]).touch(); time.sleep(60)",
                lockPath, ready
            }) info.ArgumentList.Add(argument);
        }
        using var holder = Process.Start(info)!;
        Task<ExplorerConfigurationStore>? waiting = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (!File.Exists(ready))
            {
                Assert.False(holder.HasExited, "Lock helper exited before acquiring its file handle.");
                await Task.Delay(50, timeout.Token);
            }
            waiting = ExplorerConfigurationStore.OpenAsync(root, new TestProtector(), token);
            await Task.Delay(100, token);
            Assert.False(waiting.IsCompleted);
            holder.Kill();
            await holder.WaitForExitAsync(token);
            var store = await waiting.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Empty((await store.LoadAsync(token)).Storage.Profiles);
        }
        finally
        {
            if (!holder.HasExited) { holder.Kill(); await holder.WaitForExitAsync(CancellationToken.None); }
            if (waiting is not null) { try { await waiting; } catch { } }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task WaitingOpenHonorsCancellationAndCanBeRetried()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "S3Explorer.LockTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var held = File.Open(Path.Combine(root, "configuration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    ExplorerConfigurationStore.OpenAsync(root, new TestProtector(), cancel.Token));
                Assert.False(File.Exists(Path.Combine(root, "configuration.json")));
            }
            var store = await ExplorerConfigurationStore.OpenAsync(root, new TestProtector(), token);
            Assert.Empty((await store.LoadAsync(token)).Storage.Profiles);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HeldLockTimesOutWithoutCreatingAnEmptyConfiguration()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "S3Explorer.LockTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var held = File.Open(Path.Combine(root, "configuration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                ExplorerConfigurationStore.OpenAsync(root, new TestProtector(), token).WaitAsync(TimeSpan.FromSeconds(25), token));
            Assert.Contains("配置锁", error.Message);
            Assert.False(File.Exists(Path.Combine(root, "configuration.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TestProtector : IConfigurationPayloadProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
