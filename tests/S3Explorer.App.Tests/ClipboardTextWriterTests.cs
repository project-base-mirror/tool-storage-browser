using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ClipboardTextWriterTests
{
    [Fact]
    public void BusyClipboardRetriesOnTheSameStaWhileTheUiRemainsResponsive()
    {
        RunSta(async () =>
        {
            var threadId = Environment.CurrentManagedThreadId;
            var attempts = 0;
            var ticks = 0;
            using var timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += (_, _) => ticks++;
            timer.Start();

            await ClipboardTextWriter.SetTextAsync("https://cdn.example.test/中文%20file.bin", text =>
            {
                Assert.Equal(threadId, Environment.CurrentManagedThreadId);
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.Equal("https://cdn.example.test/中文%20file.bin", text);
                if (++attempts < 3)
                    throw new ExternalException("Clipboard busy");
            }, TestContext.Current.CancellationToken);

            Assert.Equal(3, attempts);
            Assert.True(ticks > 0, "Clipboard retry blocked the UI message pump.");
        });
    }

    [Fact]
    public void PersistentFailureIsBoundedAndPreservesTheOriginalException()
    {
        RunSta(async () =>
        {
            var attempts = 0;
            var failure = new ExternalException("Clipboard unavailable");
            var error = await Assert.ThrowsAsync<ExternalException>(() =>
                ClipboardTextWriter.SetTextAsync("url", _ =>
                {
                    attempts++;
                    throw failure;
                }, TestContext.Current.CancellationToken));

            Assert.Same(failure, error);
            Assert.Equal(11, attempts);
        });
    }

    [Fact]
    public void OtherErrorsAreNotRetried()
    {
        RunSta(async () =>
        {
            var attempts = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ClipboardTextWriter.SetTextAsync("url", _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("Wrong calling context");
                }, TestContext.Current.CancellationToken));
            Assert.Equal(1, attempts);
        });
    }

    [Fact]
    public void CancellationStopsPendingRetries()
    {
        RunSta(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var attempts = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ClipboardTextWriter.SetTextAsync("url", _ =>
                {
                    attempts++;
                    cancellation.Cancel();
                    throw new ExternalException("Clipboard busy");
                }, cancellation.Token));
            Assert.Equal(1, attempts);
        });
    }

    [Fact]
    public void AlreadyCancelledCopyNeverChangesClipboard()
    {
        RunSta(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ClipboardTextWriter.SetTextAsync("url", _ => Assert.Fail("Unexpected write"), cancellation.Token));
        });
    }

    private static void RunSta(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new Control();
                _ = control.Handle;
                var task = action();
                var stopwatch = Stopwatch.StartNew();
                while (!task.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Application.DoEvents();
                    Thread.Sleep(1);
                }
                Assert.True(task.IsCompleted, "Clipboard test timed out.");
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception) { error = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA clipboard test thread timed out.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
