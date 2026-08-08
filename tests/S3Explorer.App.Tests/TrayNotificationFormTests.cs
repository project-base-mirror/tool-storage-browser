using System.Runtime.ExceptionServices;
using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class TrayNotificationFormTests
{
    [Fact]
    public void NotificationDisplaysProvidedContentWithoutTaskbarEntry()
    {
        RunSta(() =>
        {
            using var notification = new TrayNotificationForm(
                "传输任务已结束",
                "成功 2 项，失败 1 项。",
                warning: true,
                activated: () => { });

            var title = Assert.IsType<Label>(Assert.Single(
                notification.Controls.Find("TrayNotificationTitle", searchAllChildren: true)));
            var message = Assert.IsType<Label>(Assert.Single(
                notification.Controls.Find("TrayNotificationMessage", searchAllChildren: true)));

            Assert.Equal("传输任务已结束", title.Text);
            Assert.Equal("成功 2 项，失败 1 项。", message.Text);
            Assert.False(notification.ShowInTaskbar);
            Assert.True(notification.TopMost);
        });
    }

    [Fact]
    public void LocationUsesWorkingAreaWithStableMargin()
    {
        var location = TrayNotificationForm.CalculateLocation(
            new Rectangle(100, 50, 1600, 900),
            new Size(360, 96));

        Assert.Equal(new Point(1328, 842), location);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
