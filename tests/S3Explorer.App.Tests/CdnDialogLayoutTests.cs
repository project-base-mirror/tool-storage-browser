using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class CdnDialogLayoutTests
{
    [Fact]
    public void ConfigurationCenterKeepsPrimaryActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var storage = new ConnectionProfile
            {
                Name = "site-storage",
                Endpoint = "https://s3.example.com"
            };
            var credential = new CdnCredential
            {
                Name = "purge-token",
                AuthenticationType = CdnAuthenticationType.BearerToken,
                Secret = "test-only"
            };
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                CredentialId = credential.Id,
                PurgeEndpointTemplate = "https://api.example.com/purge?url={url}"
            };
            var binding = new CdnBinding
            {
                StorageProfileId = storage.Id,
                Bucket = "site",
                SourcePrefix = "assets/",
                CdnProfileId = profile.Id,
                CdnPathPrefix = "static/"
            };
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CdnConfigurationDialog(
                [storage],
                new CdnConfiguration([profile], [binding]),
                [credential],
                storage,
                "site");
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var tabs = Assert.IsType<TabControl>(
                Assert.Single(dialog.Controls.Find("CdnConfigurationTabs", searchAllChildren: true)));
            Assert.Equal(3, tabs.TabPages.Count);
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnProfileButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnCredentialButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnBindingButton"));
            var save = FindButton(dialog, "SaveCdnConfigurationButton");
            AssertButtonIsReadable(dialog, save);
            Assert.Same(save, dialog.AcceptButton);
        });
    }

    [Fact]
    public void DownloadTestKeepsTestAndCloseActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var profile = new CdnProfile { Name = "site-cdn", BaseUrl = "https://cdn.example.com" };
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CdnDownloadTestDialog(
                new StubDeliveryService(),
                profile,
                null,
                new Uri("https://cdn.example.com/assets/app.js"));
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var run = FindButton(dialog, "RunCdnDownloadTestButton");
            var close = FindButton(dialog, "CloseCdnDownloadTestButton");
            AssertButtonIsReadable(dialog, run);
            AssertButtonIsReadable(dialog, close);
            Assert.Same(run, dialog.AcceptButton);
            Assert.Same(close, dialog.CancelButton);
        });
    }

    private static Button FindButton(Control root, string name) =>
        Assert.IsType<Button>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static void AssertButtonIsReadable(Form dialog, Button button)
    {
        Assert.True(button.Height >= 34, $"{button.Name} height was {button.Height}.");
        Assert.True(button.Width >= button.PreferredSize.Width,
            $"{button.Name} width {button.Width} was smaller than preferred width {button.PreferredSize.Width}.");
        Assert.True(button.Height >= button.PreferredSize.Height,
            $"{button.Name} height {button.Height} was smaller than preferred height {button.PreferredSize.Height}.");

        var bounds = button.Bounds;
        var hierarchy = new List<string>
        {
            $"{button.Name}={button.Bounds}"
        };
        for (var parent = button.Parent; parent is not null && parent != dialog; parent = parent.Parent)
        {
            bounds.Offset(parent.Left, parent.Top);
            hierarchy.Add($"{parent.Name}/{parent.GetType().Name}={parent.Bounds}; Client={parent.ClientRectangle}");
        }
        Assert.True(dialog.ClientRectangle.Contains(bounds),
            $"{button.Name} bounds {bounds} were outside {dialog.ClientRectangle}. Hierarchy: {string.Join(" -> ", hierarchy)}");
    }

    private static void PerformLayout(Control control)
    {
        control.CreateControl();
        control.PerformLayout();
        foreach (Control child in control.Controls)
            PerformLayout(child);
        control.PerformLayout();
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class StubDeliveryService : ICdnDeliveryService
    {
        public Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnProbeResult(
                url,
                url,
                206,
                "Partial Content",
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20),
                1024,
                1024,
                "application/javascript",
                "X-Cache: HIT",
                new Dictionary<string, string> { ["X-Cache"] = "HIT" }));

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnOperationResult(true, 200, TimeSpan.Zero, 0, "ok"));

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnOperationResult(true, 200, TimeSpan.Zero, 0, "ok"));
    }
}
