using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using S3Explorer.Core;

namespace S3Explorer.App;

internal static class ObjectPreviewPolicy
{
    public const long TextLimit = 2L * 1024 * 1024;
    public const long ImageLimit = 8L * 1024 * 1024;
    public const long MaximumImagePixels = 16_000_000;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".json", ".csv", ".tsv", ".log", ".md", ".xml", ".yaml", ".yml", ".ini", ".conf", ".sql", ".html", ".css", ".js" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp" };

    public static string ExistingStorageClass(string? value) => string.IsNullOrWhiteSpace(value) ? "STANDARD" : value;

    public static string? ProtectedObjectReadOnlyReason(ObjectProperties properties)
    {
        if (!string.IsNullOrWhiteSpace(properties.EncryptionKeyId) ||
            !string.IsNullOrWhiteSpace(properties.CustomerEncryptionMethod) ||
            (!string.IsNullOrWhiteSpace(properties.ServerSideEncryptionMethod) &&
             !string.Equals(properties.ServerSideEncryptionMethod, "AES256", StringComparison.OrdinalIgnoreCase)))
            return "对象使用 KMS、客户密钥或其他专用加密，仅可预览，不能在此回传编辑。";
        if (!string.IsNullOrWhiteSpace(properties.ObjectLockMode) ||
            string.Equals(properties.ObjectLockLegalHoldStatus, "ON", StringComparison.OrdinalIgnoreCase))
            return "对象具有 Object Lock / Legal Hold 保护，仅可预览，不能在此回传编辑。";
        return null;
    }

    public static bool RequiresSseS3Encryption(ObjectProperties properties) =>
        string.Equals(properties.ServerSideEncryptionMethod, "AES256", StringComparison.OrdinalIgnoreCase);

    public static string? EditingReadOnlyReason(S3ServiceType provider, ObjectProperties properties) =>
        ProtectedObjectReadOnlyReason(properties) ??
        (provider == S3ServiceType.CloudflareR2 && RequiresSseS3Encryption(properties)
            ? "此 R2 对象报告 AES256，但尚未验证显式保留该加密设置的写入协议，仅可预览。" : null);

    public static bool ShouldRetainEditedFile(string path, byte[]? confirmedBytes)
    {
        if (!File.Exists(path)) return false;
        if (confirmedBytes is null) return true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length != confirmedBytes.LongLength) return true;
            var buffer = new byte[confirmedBytes.Length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0) return true;
                offset += read;
            }
            return stream.ReadByte() != -1 || !buffer.AsSpan().SequenceEqual(confirmedBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true; // Never delete edits whose upload/unchanged status cannot be established.
        }
    }

    public static bool IsImage(string key) => ImageExtensions.Contains(Path.GetExtension(key));
    public static bool IsText(string key) => TextExtensions.Contains(Path.GetExtension(key));
    public static long MaximumBytes(string key) => IsImage(key) ? ImageLimit : IsText(key) ? TextLimit : 0;

    public static string DecodeText(byte[] bytes)
    {
        if (bytes.LongLength > TextLimit) throw new InvalidDataException("文本超过 2 MiB 预览上限。");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (text.Contains('\0')) throw new InvalidDataException("包含二进制数据，不能按文本预览或编辑。");
        return text;
    }

    public static void ValidateImageBeforeDecode(string key, byte[] bytes)
    {
        if (bytes.LongLength > ImageLimit) throw new InvalidDataException("图片超过 8 MiB 上限。");
        var span = bytes.AsSpan();
        int width = 0, height = 0;
        switch (Path.GetExtension(key).ToLowerInvariant())
        {
            case ".png":
                if (span.Length < 33 || !span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                    !span.Slice(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4)) != 13)
                    throw new InvalidDataException("无效 PNG 文件头。");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(16, 4)));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(20, 4)));
                var ended = false;
                for (var offset = 8; offset < span.Length;)
                {
                    if (span.Length - offset < 12) throw new InvalidDataException("PNG 块被截断。");
                    var length = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
                    if (length > span.Length - offset - 12) throw new InvalidDataException("PNG 块长度无效。");
                    if (offset != 8 && span.Slice(offset + 4, 4).SequenceEqual("IHDR"u8))
                        throw new InvalidDataException("PNG 含多个尺寸头，拒绝预览。");
                    if (span.Slice(offset + 4, 4).SequenceEqual("IEND"u8))
                    {
                        if (length != 0 || offset + 12 != span.Length) throw new InvalidDataException("PNG 结束块或尾随内容无效。");
                        ended = true;
                    }
                    if (span.Slice(offset + 4, 4).SequenceEqual("acTL"u8))
                        throw new InvalidDataException("暂不预览动画 PNG。");
                    offset += checked((int)length + 12);
                }
                if (!ended) throw new InvalidDataException("PNG 缺少结束块。");
                break;
            case ".jpg":
            case ".jpeg":
                if (span.Length < 4 || span[0] != 0xff || span[1] != 0xd8) throw new InvalidDataException("无效 JPEG 文件头。");
                for (var offset = 2; offset < span.Length;)
                {
                    if (span[offset++] != 0xff) throw new InvalidDataException("无效 JPEG 标记。");
                    while (offset < span.Length && span[offset] == 0xff) offset++;
                    if (offset >= span.Length) break;
                    var marker = span[offset++];
                    if (marker is 0xda or 0xd9) break;
                    if (span.Length - offset < 2) throw new InvalidDataException("JPEG 块被截断。");
                    var length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
                    if (length < 2 || length > span.Length - offset) throw new InvalidDataException("JPEG 块长度无效。");
                    if (marker == 0xe2 && length >= 6 && span.Slice(offset + 2, 4).SequenceEqual("MPF\0"u8))
                        throw new InvalidDataException("暂不预览多图片 JPEG。");
                    if (marker is 0xc0 or 0xc1 or 0xc2)
                    {
                        if (length < 8 || width != 0) throw new InvalidDataException("无效或多帧 JPEG。");
                        height = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 3, 2));
                        width = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 5, 2));
                    }
                    else if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                        throw new InvalidDataException("暂不预览此 JPEG 编码。");
                    offset += length;
                }
                break;
            case ".bmp":
                if (span.Length < 54 || span[0] != 'B' || span[1] != 'M' ||
                    BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(14, 4)) is not (40 or 52 or 56 or 108 or 124) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(30, 4)) != 0)
                    throw new InvalidDataException("仅预览未压缩的标准 BMP。");
                width = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(18, 4));
                height = checked((int)Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(span.Slice(22, 4))));
                break;
            default:
                throw new InvalidDataException("暂不预览 GIF、动画或多页图片。");
        }
        if (width <= 0 || height <= 0 || (long)width * height > MaximumImagePixels)
            throw new InvalidDataException("图片尺寸无效或超过 1600 万像素，未启动图片解码。");
    }

    public static ProcessStartInfo EditorStartInfo(string path)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"))
        {
            UseShellExecute = false
        };
        info.ArgumentList.Add(path);
        return info;
    }
}

internal sealed record ObjectPreviewContent(string Key, byte[] Bytes, bool CanEdit, string EditExplanation);

internal sealed class ObjectPreviewDialog : Form
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(840, 0) };
    private readonly Button _edit = new() { Text = "使用记事本编辑...", AutoSize = true, Enabled = false };
    private readonly Func<CancellationToken, Task<ObjectPreviewContent>> _load;
    private readonly Func<CancellationToken, Task> _editContent;
    private Image? _image;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? EditedFilePath { get; set; }

    public ObjectPreviewDialog(string key, Func<CancellationToken, Task<ObjectPreviewContent>> load, Func<CancellationToken, Task> edit)
    {
        _load = load;
        _editContent = edit;
        Text = $"对象预览 — {key}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(920, 680);
        MinimumSize = new Size(600, 400);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status.Text = "正在下载预览；关闭窗口可取消。文本最多 2 MiB，图片最多 8 MiB。";
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_content, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "关闭 / 取消", AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        buttons.Controls.Add(_edit);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        Shown += (_, _) => PendingOperation = LoadContentAsync();
        _edit.Click += (_, _) => PendingOperation = EditContentAsync();
        FormClosing += (_, _) => _cancellation.Cancel();
    }

    private async Task LoadContentAsync()
    {
        try
        {
            var content = await _load(_cancellation.Token);
            if (_cancellation.IsCancellationRequested || IsDisposed) return;
            ShowContent(content);
            _edit.Enabled = content.CanEdit;
            _status.Text = $"只读预览。{content.EditExplanation}";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsDisposed) _status.Text = $"预览失败：{exception.Message}";
        }
    }

    internal void ShowContent(ObjectPreviewContent content)
    {
        if (ObjectPreviewPolicy.IsImage(content.Key))
        {
            ObjectPreviewPolicy.ValidateImageBeforeDecode(content.Key, content.Bytes);
            using var stream = new MemoryStream(content.Bytes, writable: false);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            if ((long)source.Width * source.Height > ObjectPreviewPolicy.MaximumImagePixels)
                throw new InvalidDataException("图片超过 1600 万像素，不能安全预览。");
            _image = new Bitmap(source);
            _content.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = _image, SizeMode = PictureBoxSizeMode.Zoom });
            return;
        }
        var text = ObjectPreviewPolicy.DecodeText(content.Bytes);
        if (Path.GetExtension(content.Key).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var table = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = true };
            using var reader = new StringReader(text);
            using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            var rows = new List<string[]>();
            while (!parser.EndOfData && rows.Count < 200)
                rows.Add((parser.ReadFields() ?? []).Take(50).ToArray());
            var columns = rows.Count == 0 ? 0 : rows.Max(row => row.Length);
            for (var index = 0; index < columns; index++) table.Columns.Add($"c{index}", $"列 {index + 1}");
            foreach (var row in rows) table.Rows.Add(row.Cast<object>().ToArray());
            _content.Controls.Add(table);
            return;
        }
        _content.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = text });
    }

    private async Task EditContentAsync()
    {
        _edit.Enabled = false;
        _status.Text = "在记事本中保存并关闭后，仍须确认才上传；关闭本窗口将取消上传。";
        try
        {
            await _editContent(_cancellation.Token);
            if (!IsDisposed) _status.Text = "编辑流程结束；如已上传，请关闭后重新预览。";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsDisposed) _status.Text = $"编辑 / 上传未完成：{exception.Message}" +
                (EditedFilePath is null ? "" : $" 修改文件保留在：{EditedFilePath}");
        }
        // One edit per captured remote identity: reopening refreshes identity and content.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellation.Cancel();
            _image?.Dispose();
            if (PendingOperation.IsCompleted) _cancellation.Dispose();
            // MainForm awaits PendingOperation before disposing this modal dialog.
        }
        base.Dispose(disposing);
    }
}
