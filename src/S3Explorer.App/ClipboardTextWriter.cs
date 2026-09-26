using System.Runtime.InteropServices;

namespace S3Explorer.App;

internal static class ClipboardTextWriter
{
    public static Task SetTextAsync(string text, CancellationToken cancellationToken = default) =>
        SetTextAsync(text, WriteText, cancellationToken);

    internal static async Task SetTextAsync(
        string text,
        Action<string> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(write);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                write(text);
                return;
            }
            catch (ExternalException) when (attempt < 10)
            {
                // Yield the message pump while another clipboard owner finishes.
                // The continuation must stay on the caller's STA UI thread.
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static void WriteText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, autoConvert: false, text);
        // WinForms' built-in retries sleep on the UI thread; retry asynchronously above.
        Clipboard.SetDataObject(data, copy: true, retryTimes: 0, retryDelay: 0);
    }
}
