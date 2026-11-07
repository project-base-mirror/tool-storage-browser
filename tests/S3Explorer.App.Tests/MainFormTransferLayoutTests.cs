using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    [Theory]
    [InlineData(120, 780)]
    [InlineData(190, 600)]
    [InlineData(320, 780)]
    [InlineData(480, 1000)]
    public async Task TransferPanelRestoresReadableHeightAndKeepsItWhenToggled(int savedHeight, int windowHeight)
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-transfer-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await using var queue = new PersistentTransferQueue(
                    new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new NeverRunExecutor());
                await queue.InitializeAsync();
                using var form = CreateForm(queue, new TransferRuntimeConfiguration(),
                    new SimpleFileLogger(Path.Combine(root, "logs")),
                    new SingleProfileStore(new ConnectionProfile { Name = "layout", Endpoint = "https://storage.example.test" }));
                form.Size = new Size(1280, windowHeight);
                _ = form.Handle;
                form.PerformLayout();
                SetField(form, "_settings", new AppSettings { TransferPanelHeight = savedHeight });
                var setVisibility = typeof(MainForm).GetMethod("SetTransferVisibility", BindingFlags.Instance | BindingFlags.NonPublic)!;
                setVisibility.Invoke(form, [true]);
                form.PerformLayout();
                var split = GetField<SplitContainer>(form, "_outerSplit");
                Assert.True(split.Panel2.Height >= form.LogicalToDeviceUnits(AppSettings.MinimumTransferPanelHeight));
                Assert.True(split.Panel1.Height >= split.Panel1MinSize);
                var height = split.Panel2.Height;
                if (savedHeight == 480) Assert.Equal(savedHeight, height);
                setVisibility.Invoke(form, [false]);
                setVisibility.Invoke(form, [true]);
                Assert.Equal(height, split.Panel2.Height);
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }
}
