using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace S3Explorer.App;

internal enum UiIconKind
{
    NewConnection,
    Connect,
    Back,
    Forward,
    Up,
    Refresh,
    Bucket,
    Folder,
    Upload,
    Download,
    Copy,
    Move,
    Delete,
    Properties,
    Transfers,
    Settings,
    Account,
    Accounts,
    File,
    Info
}

internal static class UiIcons
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    public static Image Create(UiIconKind kind, int size = 20)
    {
        return kind switch
        {
            UiIconKind.Folder => ShellImage("folder", true, size) ?? DrawVector(kind, size),
            UiIconKind.Bucket => ShellImage("bucket", true, size) ?? DrawVector(kind, size),
            UiIconKind.File => ShellImage("file.bin", false, size) ?? DrawVector(kind, size),
            UiIconKind.Copy => ShellImage("file.txt", false, size) ?? DrawVector(kind, size),
            UiIconKind.Move => ShellImage("folder", true, size) ?? DrawVector(kind, size),
            UiIconKind.Properties or UiIconKind.Info => Scale(SystemIcons.Information, size),
            UiIconKind.Settings => Scale(SystemIcons.Application, size),
            UiIconKind.Account or UiIconKind.Accounts or UiIconKind.Connect => Scale(SystemIcons.Shield, size),
            _ => DrawVector(kind, size)
        };
    }

    public static Icon CreateApplicationIcon()
    {
        const int size = 64;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var background = new SolidBrush(Color.FromArgb(33, 113, 181));
        graphics.FillEllipse(background, 2, 2, 60, 60);

        using var storagePen = new Pen(Color.White, 4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawEllipse(storagePen, 14, 14, 36, 14);
        graphics.DrawLine(storagePen, 14, 21, 14, 43);
        graphics.DrawLine(storagePen, 50, 21, 50, 43);
        graphics.DrawArc(storagePen, 14, 25, 36, 18, 0, 180);
        graphics.DrawArc(storagePen, 14, 34, 36, 18, 0, 180);

        using var statusBrush = new SolidBrush(Color.FromArgb(43, 190, 105));
        graphics.FillEllipse(statusBrush, 43, 43, 15, 15);
        using var statusPen = new Pen(Color.White, 2f);
        graphics.DrawEllipse(statusPen, 43, 43, 15, 15);

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static ImageList CreateSmallImageList()
    {
        var images = new ImageList
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(16, 16),
            TransparentColor = Color.Transparent
        };

        Add(images, "accounts", UiIconKind.Accounts);
        Add(images, "account", UiIconKind.Account);
        Add(images, "connect", UiIconKind.Connect);
        Add(images, "bucket", UiIconKind.Bucket);
        Add(images, "folder", UiIconKind.Folder);
        Add(images, "file", UiIconKind.File);
        AddShell(images, "file-text", "file.txt");
        AddShell(images, "file-image", "file.png");
        AddShell(images, "file-archive", "file.zip");
        AddShell(images, "file-audio", "file.mp3");
        AddShell(images, "file-video", "file.mp4");
        AddShell(images, "file-code", "file.cs");
        Add(images, "refresh", UiIconKind.Refresh);
        Add(images, "info", UiIconKind.Info);
        return images;
    }

    public static string ObjectImageKey(string name, bool isDirectory)
    {
        if (isDirectory) return "folder";
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".log" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml" => "file-text",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "file-image",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" => "file-archive",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "file-audio",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "file-video",
            ".cs" or ".fs" or ".vb" or ".js" or ".ts" or ".py" or ".go" or ".java" or ".cpp" or ".h" or ".ps1" or ".sh" => "file-code",
            _ => "file"
        };
    }

    private static void Add(ImageList images, string key, UiIconKind kind) => images.Images.Add(key, Create(kind, 16));

    private static void AddShell(ImageList images, string key, string fileName) =>
        images.Images.Add(key, ShellImage(fileName, false, 16) ?? Create(UiIconKind.File, 16));

    private static Image Scale(Icon icon, int size)
    {
        using var source = icon.ToBitmap();
        return new Bitmap(source, new Size(size, size));
    }

    private static Image? ShellImage(string path, bool directory, int size)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(
            path,
            directory ? FileAttributeDirectory : FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;

        try
        {
            using var icon = (Icon)Icon.FromHandle(info.IconHandle).Clone();
            using var source = icon.ToBitmap();
            return new Bitmap(source, new Size(size, size));
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static Image DrawVector(UiIconKind kind, int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var stroke = Math.Max(1.5f, size / 10f);
        using var pen = new Pen(SystemColors.ControlText, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var accent = new Pen(SystemColors.Highlight, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var left = size * 0.2f;
        var right = size * 0.8f;
        var top = size * 0.2f;
        var bottom = size * 0.8f;
        var middle = size * 0.5f;

        switch (kind)
        {
            case UiIconKind.Back:
                DrawArrow(graphics, pen, right, middle, left, middle);
                break;
            case UiIconKind.Forward:
                DrawArrow(graphics, pen, left, middle, right, middle);
                break;
            case UiIconKind.Up:
                DrawArrow(graphics, pen, middle, bottom, middle, top);
                break;
            case UiIconKind.Refresh:
                graphics.DrawArc(pen, left, top, right - left, bottom - top, 35, 285);
                graphics.DrawLine(pen, right, top + size * 0.15f, right, top + size * 0.38f);
                graphics.DrawLine(pen, right, top + size * 0.15f, right - size * 0.2f, top + size * 0.15f);
                break;
            case UiIconKind.Upload:
                DrawArrow(graphics, accent, middle, bottom - size * 0.08f, middle, top);
                graphics.DrawLine(pen, left, bottom, right, bottom);
                break;
            case UiIconKind.Download:
                DrawArrow(graphics, accent, middle, top, middle, bottom - size * 0.08f);
                graphics.DrawLine(pen, left, bottom, right, bottom);
                break;
            case UiIconKind.NewConnection:
                graphics.DrawRectangle(pen, left, top, size * 0.45f, size * 0.6f);
                graphics.DrawLine(accent, size * 0.7f, middle, size * 0.95f, middle);
                graphics.DrawLine(accent, size * 0.825f, middle - size * 0.125f, size * 0.825f, middle + size * 0.125f);
                break;
            case UiIconKind.Delete:
                graphics.DrawRectangle(pen, left + size * 0.08f, top + size * 0.18f, size * 0.44f, size * 0.52f);
                graphics.DrawLine(pen, left, top + size * 0.12f, right - size * 0.08f, top + size * 0.12f);
                graphics.DrawLine(pen, middle - size * 0.13f, top, middle + size * 0.13f, top);
                break;
            case UiIconKind.Transfers:
                graphics.DrawLine(pen, left, top, right, top);
                graphics.DrawLine(pen, left, middle, right, middle);
                graphics.DrawLine(pen, left, bottom, right, bottom);
                graphics.DrawLine(accent, right - size * 0.18f, top - size * 0.08f, right, top);
                graphics.DrawLine(accent, right - size * 0.18f, top + size * 0.08f, right, top);
                break;
            default:
                graphics.DrawRectangle(pen, left, top, right - left, bottom - top);
                break;
        }
        return bitmap;
    }

    private static void DrawArrow(Graphics graphics, Pen pen, float fromX, float fromY, float toX, float toY)
    {
        graphics.DrawLine(pen, fromX, fromY, toX, toY);
        var angle = Math.Atan2(toY - fromY, toX - fromX);
        var length = pen.Width * 3.5f;
        graphics.DrawLine(pen, toX, toY, toX - (float)Math.Cos(angle - Math.PI / 4) * length, toY - (float)Math.Sin(angle - Math.PI / 4) * length);
        graphics.DrawLine(pen, toX, toY, toX - (float)Math.Cos(angle + Math.PI / 4) * length, toY - (float)Math.Sin(angle + Math.PI / 4) * length);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo info, uint infoSize, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
