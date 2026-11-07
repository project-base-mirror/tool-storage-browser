using System.ComponentModel;
using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    [Fact]
    public async Task ObjectDownloadContextMenuSupportsFilesFoldersAndMultipleSelectionWithoutCdn()
    {
        await RunMessagePumpAsync(async unusedHost =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-download-menu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);
                await using var queue = new PersistentTransferQueue(
                    new JsonTransferTaskStore(Path.Combine(root, "tasks.json")), new NeverRunExecutor());
                await queue.InitializeAsync(TestContext.Current.CancellationToken);
                using var form = CreateForm(queue, new TransferRuntimeConfiguration(),
                    new SimpleFileLogger(Path.Combine(root, "logs")), new SingleProfileStore(profile));
                SetField(form, "_currentProfile", profile);
                SetField(form, "_currentBucket", "bucket");
                var objects = GetField<ListView>(form, "_objects");
                _ = form.Handle;
                _ = objects.Handle;
                var menu = GetField<ContextMenuStrip>(form, "_objectMenu");
                var download = Assert.Single(menu.Items.OfType<ToolStripMenuItem>(),
                    item => item.Name == "ObjectDownloadContextMenu");
                Assert.Equal("下载...", download.Text);
                Assert.NotNull(download.Image);
                Assert.Empty(download.DropDownItems);
                Assert.True(menu.Items.IndexOf(download) < menu.Items.IndexOf(
                    FindMenuItem(menu.Items, "CdnObjectContextMenu")));

                // This menu is independent of CDN configuration, and adding it must not shift
                // the existing index-based enablement of version history.
                var versions = Assert.IsType<ToolStripMenuItem>(menu.Items[11]);
                Assert.Equal("查看 / 恢复历史版本...", versions.Text);
                void OpenMenu() => typeof(ToolStripDropDown).GetMethod("OnOpening",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, [new CancelEventArgs()]);
                void Select(params S3ObjectEntry[] entries)
                {
                    objects.Items.Clear();
                    foreach (var entry in entries)
                    {
                        var item = new ListViewItem(entry.Name) { Tag = entry };
                        objects.Items.Add(item);
                        item.Selected = true;
                    }
                    OpenMenu();
                }

                Select();
                Assert.False(download.Enabled);
                var file = new S3ObjectEntry("prefix/file.txt", "file.txt", 12, false, null, "STANDARD");
                var folder = new S3ObjectEntry("prefix/folder/", "folder", 0, true, null, "STANDARD");
                Select(file);
                Assert.True(download.Enabled);
                Assert.True(versions.Enabled);
                Select(folder);
                Assert.True(download.Enabled);
                Assert.False(versions.Enabled);
                Select(file, folder);
                Assert.True(download.Enabled);
                Assert.False(versions.Enabled);

                SetField(form, "_currentBucket", null);
                OpenMenu();
                Assert.False(download.Enabled);
                SetField(form, "_currentBucket", "bucket");
                SetField(form, "_currentProfile", null);
                OpenMenu();
                Assert.False(download.Enabled);
                SetField(form, "_currentProfile", profile);
                OpenMenu();
                Assert.True(download.Enabled);
                Select();
                Assert.False(download.Enabled);
                Assert.Empty(queue.Snapshot.Tasks);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }
}
