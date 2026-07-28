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
                "site",
                new StubCertificateInspector());
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            var tabs = Assert.IsType<TabControl>(
                Assert.Single(dialog.Controls.Find("CdnConfigurationTabs", searchAllChildren: true)));
            Assert.Equal(3, tabs.TabPages.Count);
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnProfileButton"));
            var certificate = FindButton(dialog, "CheckCdnCertificateButton");
            AssertButtonIsReadable(dialog, certificate);
            Assert.True(certificate.Enabled);
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnCredentialButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "AddCdnBindingButton"));
            var save = FindButton(dialog, "SaveCdnConfigurationButton");
            AssertButtonIsReadable(dialog, save);
            Assert.Same(save, dialog.AcceptButton);
        });
    }

    [Fact]
    public void CertificateResultKeepsDetailsAndActionsReadableAtLargeText()
    {
        RunSta(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var result = new CdnCertificateCheckResult(
                new Uri("https://cdn.example.com"),
                now,
                now.AddDays(-30),
                now.AddDays(12),
                "CN=cdn.example.com",
                "CN=Example CA",
                new string('A', 64),
                "Tls13",
                CdnCertificateProblems.None,
                []);
            using var largerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F);
            using var dialog = new CdnCertificateResultDialog(result);
            dialog.Font = largerFont;
            dialog.Size = dialog.MinimumSize;
            PerformLayout(dialog);

            AssertButtonIsReadable(dialog, FindButton(dialog, "CopyCdnCertificateResultButton"));
            AssertButtonIsReadable(dialog, FindButton(dialog, "CloseCdnCertificateResultButton"));
            var details = Assert.IsType<TextBox>(Assert.Single(
                dialog.Controls.Find("CdnCertificateResultDetails", searchAllChildren: true)));
            Assert.Contains("到期时间", details.Text, StringComparison.Ordinal);
            Assert.Contains("剩余天数：12", details.Text, StringComparison.Ordinal);
            Assert.Contains("吊销状态：未检查", details.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CertificateCheckCanBeCancelledWithoutClosingConfiguration()
    {
        RunSta(() =>
        {
            var inspector = new BlockingCertificateInspector();
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com"
            };
            using var dialog = new CdnConfigurationDialog(
                [],
                new CdnConfiguration([profile], []),
                [],
                certificateInspector: inspector);
            dialog.Show();

            var check = FindButton(dialog, "CheckCdnCertificateButton");
            check.PerformClick();
            WaitUntil(() => inspector.Started && check.Text == "取消证书检测");
            check.PerformClick();
            var grid = Assert.IsType<DataGridView>(Assert.Single(
                dialog.Controls.Find("CdnProfilesTabGrid", searchAllChildren: true)));
            WaitUntil(() => grid.Rows[0].Cells["certificate"].Value?.ToString() == "检测已取消");

            Assert.False(dialog.IsDisposed);
            Assert.Equal("检测 HTTPS 证书", check.Text);
            Assert.True(check.Enabled);
            dialog.Close();
        });
    }

    [Fact]
    public void ProfileEditorLoadsAndSavesNotes()
    {
        RunSta(() =>
        {
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                Notes = "原备注"
            };
            using var dialog = new CdnProfileEditorDialog(profile, []);
            var notes = Assert.IsType<TextBox>(Assert.Single(
                dialog.Controls.Find("CdnProfileNotes", searchAllChildren: true)));

            Assert.Equal("原备注", notes.Text);
            Assert.True(notes.Multiline);
            Assert.Equal(CdnProfile.MaximumNotesLength, notes.MaxLength);
            notes.Text = "发布域名，证书由平台团队维护。";
            dialog.Show();
            FindButton(dialog, "SaveCdnProfileButton").PerformClick();

            Assert.Equal(DialogResult.OK, dialog.DialogResult);
            Assert.Equal("发布域名，证书由平台团队维护。", dialog.Profile.Notes);
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
            var cancel = FindButton(dialog, "CancelCdnDownloadTestButton");
            var close = FindButton(dialog, "CloseCdnDownloadTestButton");
            AssertButtonIsReadable(dialog, run);
            AssertButtonIsReadable(dialog, cancel);
            AssertButtonIsReadable(dialog, close);
            var timeout = Assert.IsType<NumericUpDown>(Assert.Single(
                dialog.Controls.Find("CdnDownloadTimeoutSeconds", searchAllChildren: true)));
            var status = Assert.IsType<Label>(Assert.Single(
                dialog.Controls.Find("CdnDownloadTestStatus", searchAllChildren: true)));
            Assert.Equal(profile.TimeoutSeconds, timeout.Value);
            var footer = Assert.IsType<TableLayoutPanel>(status.Parent);
            var actions = Assert.IsType<FlowLayoutPanel>(run.Parent);
            Assert.Equal(0, footer.GetColumn(status));
            Assert.Equal(1, footer.GetColumn(actions));
            Assert.Same(run, dialog.AcceptButton);
            Assert.Same(close, dialog.CancelButton);
        });
    }

    [Fact]
    public void DownloadTestCanBeCancelledWithoutClosingDialog()
    {
        RunSta(() =>
        {
            var service = new BlockingDeliveryService();
            var profile = new CdnProfile { Name = "site-cdn", BaseUrl = "https://cdn.example.com" };
            using var dialog = new CdnDownloadTestDialog(
                service,
                profile,
                null,
                new Uri("https://cdn.example.com/assets/app.js"));
            dialog.Show();

            WaitUntil(() => service.Started && FindButton(dialog, "CancelCdnDownloadTestButton").Enabled);
            FindButton(dialog, "CancelCdnDownloadTestButton").PerformClick();
            WaitUntil(() => FindLabel(dialog, "CdnDownloadTestStatus").Text == "测试已取消");

            Assert.True(FindButton(dialog, "RunCdnDownloadTestButton").Enabled);
            Assert.False(FindButton(dialog, "CancelCdnDownloadTestButton").Enabled);
            Assert.False(dialog.IsDisposed);
            dialog.Close();
        });
    }

    [Fact]
    public void DownloadTestReportsConfiguredTimeout()
    {
        RunSta(() =>
        {
            var service = new BlockingDeliveryService();
            var profile = new CdnProfile
            {
                Name = "site-cdn",
                BaseUrl = "https://cdn.example.com",
                TimeoutSeconds = 1
            };
            using var dialog = new CdnDownloadTestDialog(
                service,
                profile,
                null,
                new Uri("https://cdn.example.com/assets/app.js"));
            dialog.Show();

            WaitUntil(
                () => FindLabel(dialog, "CdnDownloadTestStatus").Text == "测试超时（1 秒）",
                TimeSpan.FromSeconds(5));

            Assert.True(FindButton(dialog, "RunCdnDownloadTestButton").Enabled);
            Assert.False(FindButton(dialog, "CancelCdnDownloadTestButton").Enabled);
            dialog.Close();
        });
    }

    private static Button FindButton(Control root, string name) =>
        Assert.IsType<Button>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static Label FindLabel(Control root, string name) =>
        Assert.IsType<Label>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static void WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected dialog state was not reached.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

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

    private sealed class BlockingDeliveryService : ICdnDeliveryService
    {
        public bool Started { get; private set; }

        public async Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken)
        {
            Started = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking probe should only finish through cancellation.");
        }

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCertificateInspector : ICdnCertificateInspector
    {
        public Task<CdnCertificateCheckResult> InspectAsync(
            CdnProfile profile,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CdnCertificateCheckResult(
                new Uri(profile.BaseUrl),
                now,
                now.AddDays(-30),
                now.AddDays(60),
                "CN=cdn.example.com",
                "CN=Example CA",
                new string('A', 64),
                "Tls13",
                CdnCertificateProblems.None,
            []));
        }
    }

    private sealed class BlockingCertificateInspector : ICdnCertificateInspector
    {
        public bool Started { get; private set; }

        public async Task<CdnCertificateCheckResult> InspectAsync(
            CdnProfile profile,
            CancellationToken cancellationToken)
        {
            Started = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The certificate check should finish through cancellation.");
        }
    }
}
