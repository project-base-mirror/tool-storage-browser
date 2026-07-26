using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    Info,
    Sync,
    Analyze,
    Log,
    Diagnostics,
    Paste,
    Help
}

internal static class UiIcons
{
    private static readonly Color Ink = Color.FromArgb(55, 65, 81);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Positive = Color.FromArgb(22, 163, 74);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private static readonly Color Storage = Color.FromArgb(14, 116, 144);

    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    public static Image Create(UiIconKind kind, int size = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 12);
        const int scale = 4;
        using var source = DrawGlyph(kind, size * scale);
        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        return result;
    }

    public static Image? ForCommand(string id, int size = 16)
    {
        var kind = id switch
        {
            "new-connection" => UiIconKind.NewConnection,
            "edit-connection" or "properties" or "properties-menu" or "bucket-properties" => UiIconKind.Properties,
            "delete-connection" or "delete-object" or "delete-object-menu" or "delete-bucket" or "empty-bucket" => UiIconKind.Delete,
            "connect" or "disconnect" => UiIconKind.Connect,
            "refresh" or "refresh-buckets" => UiIconKind.Refresh,
            "back" => UiIconKind.Back,
            "forward" => UiIconKind.Forward,
            "up" => UiIconKind.Up,
            "create-bucket" => UiIconKind.Bucket,
            "create-folder" => UiIconKind.Folder,
            "upload-file" or "upload-folder" => UiIconKind.Upload,
            "download" => UiIconKind.Download,
            "clipboard-copy" or "copy-object" or "copy-path" or "copy-url" or "copy-key" => UiIconKind.Copy,
            "clipboard-paste" => UiIconKind.Paste,
            "clipboard-cut" or "move-object" or "rename" or "rename-object" => UiIconKind.Move,
            "transfer-queue" or "failed-transfers" or "multipart-uploads" => UiIconKind.Transfers,
            "folder-sync" => UiIconKind.Sync,
            "settings" => UiIconKind.Settings,
            "logs" => UiIconKind.Log,
            "diagnostics" => UiIconKind.Diagnostics,
            "help" => UiIconKind.Help,
            "bucket-acl" or "bucket-policy" or "bucket-access-controls" or "metadata" or "presign" => UiIconKind.Info,
            _ => (UiIconKind?)null
        };
        return kind is null ? null : Create(kind.Value, size);
    }

    public static Icon CreateApplicationIcon()
    {
        const int size = 64;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new LinearGradientBrush(
            new Rectangle(2, 2, 60, 60), Color.FromArgb(37, 99, 235), Color.FromArgb(14, 116, 144), 45f);
        graphics.FillRoundedRectangle(background, new RectangleF(2, 2, 60, 60), 14);
        using var pen = RoundedPen(Color.White, 4f);
        DrawStorage(graphics, pen, new RectangleF(14, 14, 36, 34));
        using var statusBrush = new SolidBrush(Color.FromArgb(34, 197, 94));
        graphics.FillEllipse(statusBrush, 43, 43, 15, 15);
        using var statusPen = RoundedPen(Color.White, 2f);
        graphics.DrawEllipse(statusPen, 43, 43, 15, 15);

        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
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

    private static Bitmap DrawGlyph(UiIconKind kind, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var stroke = size / 12f;
        using var ink = RoundedPen(Ink, stroke);
        using var accent = RoundedPen(Accent, stroke);
        using var positive = RoundedPen(Positive, stroke);
        using var danger = RoundedPen(Danger, stroke);
        using var storage = RoundedPen(Storage, stroke);

        var left = size * .18f;
        var right = size * .82f;
        var top = size * .18f;
        var bottom = size * .82f;
        var middle = size * .5f;

        switch (kind)
        {
            case UiIconKind.Back:
                DrawArrow(graphics, ink, right, middle, left, middle);
                break;
            case UiIconKind.Forward:
                DrawArrow(graphics, ink, left, middle, right, middle);
                break;
            case UiIconKind.Up:
                DrawArrow(graphics, ink, middle, bottom, middle, top);
                break;
            case UiIconKind.Refresh:
                graphics.DrawArc(accent, left, top, right - left, bottom - top, 35, 285);
                graphics.DrawLines(accent, [
                    new PointF(right, top + size * .1f),
                    new PointF(right, top + size * .32f),
                    new PointF(right - size * .22f, top + size * .32f)]);
                break;
            case UiIconKind.Upload:
                DrawTransfer(graphics, ink, accent, size, upload: true);
                break;
            case UiIconKind.Download:
                DrawTransfer(graphics, ink, accent, size, upload: false);
                break;
            case UiIconKind.NewConnection:
                DrawPerson(graphics, ink, new RectangleF(size * .13f, size * .17f, size * .48f, size * .62f));
                DrawPlus(graphics, positive, size * .73f, size * .63f, size * .16f);
                break;
            case UiIconKind.Connect:
                graphics.DrawArc(accent, size * .15f, size * .24f, size * .43f, size * .43f, 40, 280);
                graphics.DrawArc(ink, size * .42f, size * .33f, size * .43f, size * .43f, 220, 280);
                break;
            case UiIconKind.Account:
                DrawPerson(graphics, accent, new RectangleF(left, top, right - left, bottom - top));
                break;
            case UiIconKind.Accounts:
                DrawPerson(graphics, ink, new RectangleF(size * .08f, size * .24f, size * .52f, size * .58f));
                DrawPerson(graphics, accent, new RectangleF(size * .4f, size * .14f, size * .52f, size * .58f));
                break;
            case UiIconKind.Bucket:
                DrawStorage(graphics, storage, new RectangleF(left, top, right - left, bottom - top));
                break;
            case UiIconKind.Folder:
                using (var folder = FolderPath(size))
                    graphics.DrawPath(accent, folder);
                break;
            case UiIconKind.File:
                DrawFile(graphics, ink, size, 0, 0);
                break;
            case UiIconKind.Copy:
                DrawFile(graphics, ink, size, -.08f, .08f);
                DrawFile(graphics, accent, size, .1f, -.08f);
                break;
            case UiIconKind.Move:
                DrawFile(graphics, ink, size, -.12f, 0);
                DrawArrow(graphics, accent, size * .42f, size * .62f, size * .9f, size * .62f);
                break;
            case UiIconKind.Paste:
                graphics.DrawRoundedRectangle(ink, new RectangleF(size * .22f, size * .22f, size * .58f, size * .62f), size * .06f);
                graphics.DrawRoundedRectangle(accent, new RectangleF(size * .35f, size * .12f, size * .3f, size * .18f), size * .04f);
                break;
            case UiIconKind.Delete:
                graphics.DrawRoundedRectangle(danger, new RectangleF(size * .28f, size * .3f, size * .44f, size * .52f), size * .04f);
                graphics.DrawLine(danger, size * .2f, size * .25f, size * .8f, size * .25f);
                graphics.DrawLine(danger, size * .4f, size * .16f, size * .6f, size * .16f);
                graphics.DrawLine(danger, size * .43f, size * .42f, size * .43f, size * .7f);
                graphics.DrawLine(danger, size * .57f, size * .42f, size * .57f, size * .7f);
                break;
            case UiIconKind.Properties:
            case UiIconKind.Info:
                graphics.DrawEllipse(accent, left, top, right - left, bottom - top);
                graphics.DrawLine(accent, middle, size * .44f, middle, size * .7f);
                graphics.DrawEllipse(accent, middle - stroke / 2, size * .3f, stroke, stroke);
                break;
            case UiIconKind.Settings:
                graphics.DrawEllipse(ink, size * .34f, size * .34f, size * .32f, size * .32f);
                for (var index = 0; index < 8; index++)
                {
                    var angle = index * Math.PI / 4;
                    graphics.DrawLine(accent,
                        middle + (float)Math.Cos(angle) * size * .25f,
                        middle + (float)Math.Sin(angle) * size * .25f,
                        middle + (float)Math.Cos(angle) * size * .38f,
                        middle + (float)Math.Sin(angle) * size * .38f);
                }
                break;
            case UiIconKind.Transfers:
                for (var index = 0; index < 3; index++)
                {
                    var y = size * (.25f + index * .25f);
                    graphics.DrawLine(ink, left, y, size * .58f, y);
                    DrawArrow(graphics, accent, size * .58f, y, right, y);
                }
                break;
            case UiIconKind.Sync:
                graphics.DrawArc(accent, left, top, right - left, bottom - top, 205, 230);
                graphics.DrawLines(accent, [new PointF(right, size * .23f), new PointF(right, size * .43f), new PointF(size * .62f, size * .37f)]);
                graphics.DrawLines(positive, [new PointF(left, size * .77f), new PointF(left, size * .57f), new PointF(size * .38f, size * .63f)]);
                break;
            case UiIconKind.Analyze:
                graphics.DrawEllipse(accent, size * .17f, size * .15f, size * .46f, size * .46f);
                graphics.DrawLine(ink, size * .58f, size * .58f, size * .83f, size * .83f);
                break;
            case UiIconKind.Log:
                DrawFile(graphics, ink, size, 0, 0);
                graphics.DrawLine(accent, size * .32f, size * .43f, size * .68f, size * .43f);
                graphics.DrawLine(accent, size * .32f, size * .56f, size * .68f, size * .56f);
                graphics.DrawLine(accent, size * .32f, size * .69f, size * .56f, size * .69f);
                break;
            case UiIconKind.Diagnostics:
                graphics.DrawLines(accent, [
                    new PointF(left, middle), new PointF(size * .34f, middle),
                    new PointF(size * .43f, size * .27f), new PointF(size * .55f, size * .73f),
                    new PointF(size * .66f, middle), new PointF(right, middle)]);
                break;
            case UiIconKind.Help:
                graphics.DrawEllipse(accent, left, top, right - left, bottom - top);
                graphics.DrawArc(accent, size * .37f, size * .3f, size * .26f, size * .23f, 190, 250);
                graphics.DrawLine(accent, middle, size * .52f, middle, size * .61f);
                graphics.DrawEllipse(accent, middle - stroke / 2, size * .69f, stroke, stroke);
                break;
        }
        return bitmap;
    }

    private static void DrawTransfer(Graphics graphics, Pen ink, Pen accent, int size, bool upload)
    {
        graphics.DrawLines(ink, [
            new PointF(size * .2f, size * .68f), new PointF(size * .2f, size * .82f),
            new PointF(size * .8f, size * .82f), new PointF(size * .8f, size * .68f)]);
        if (upload)
            DrawArrow(graphics, accent, size * .5f, size * .68f, size * .5f, size * .16f);
        else
            DrawArrow(graphics, accent, size * .5f, size * .16f, size * .5f, size * .68f);
    }

    private static void DrawPerson(Graphics graphics, Pen pen, RectangleF bounds)
    {
        var head = new RectangleF(
            bounds.X + bounds.Width * .32f, bounds.Y,
            bounds.Width * .36f, bounds.Height * .36f);
        graphics.DrawEllipse(pen, head);
        graphics.DrawArc(pen,
            bounds.X + bounds.Width * .12f, bounds.Y + bounds.Height * .42f,
            bounds.Width * .76f, bounds.Height * .58f, 185, 170);
    }

    private static void DrawStorage(Graphics graphics, Pen pen, RectangleF bounds)
    {
        var capHeight = bounds.Height * .3f;
        graphics.DrawEllipse(pen, bounds.X, bounds.Y, bounds.Width, capHeight);
        graphics.DrawLine(pen, bounds.Left, bounds.Y + capHeight / 2, bounds.Left, bounds.Bottom - capHeight / 2);
        graphics.DrawLine(pen, bounds.Right, bounds.Y + capHeight / 2, bounds.Right, bounds.Bottom - capHeight / 2);
        graphics.DrawArc(pen, bounds.X, bounds.Bottom - capHeight, bounds.Width, capHeight, 0, 180);
        graphics.DrawArc(pen, bounds.X, bounds.Y + bounds.Height * .34f, bounds.Width, capHeight, 0, 180);
    }

    private static GraphicsPath FolderPath(int size)
    {
        var path = new GraphicsPath();
        path.AddLines([
            new PointF(size * .13f, size * .3f),
            new PointF(size * .39f, size * .3f),
            new PointF(size * .48f, size * .2f),
            new PointF(size * .75f, size * .2f),
            new PointF(size * .82f, size * .76f),
            new PointF(size * .15f, size * .76f),
            new PointF(size * .13f, size * .3f)
        ]);
        return path;
    }

    private static void DrawFile(Graphics graphics, Pen pen, int size, float offsetX, float offsetY)
    {
        var x = size * (.24f + offsetX);
        var y = size * (.16f + offsetY);
        var width = size * .5f;
        var height = size * .66f;
        var fold = size * .16f;
        graphics.DrawLines(pen, [
            new PointF(x, y), new PointF(x + width - fold, y),
            new PointF(x + width, y + fold), new PointF(x + width, y + height),
            new PointF(x, y + height), new PointF(x, y)]);
        graphics.DrawLines(pen, [
            new PointF(x + width - fold, y),
            new PointF(x + width - fold, y + fold),
            new PointF(x + width, y + fold)]);
    }

    private static void DrawPlus(Graphics graphics, Pen pen, float x, float y, float radius)
    {
        graphics.DrawLine(pen, x - radius, y, x + radius, y);
        graphics.DrawLine(pen, x, y - radius, x, y + radius);
    }

    private static void DrawArrow(Graphics graphics, Pen pen, float fromX, float fromY, float toX, float toY)
    {
        graphics.DrawLine(pen, fromX, fromY, toX, toY);
        var angle = Math.Atan2(toY - fromY, toX - fromX);
        var length = pen.Width * 3.5f;
        graphics.DrawLine(pen, toX, toY,
            toX - (float)Math.Cos(angle - Math.PI / 4) * length,
            toY - (float)Math.Sin(angle - Math.PI / 4) * length);
        graphics.DrawLine(pen, toX, toY,
            toX - (float)Math.Cos(angle + Math.PI / 4) * length,
            toY - (float)Math.Sin(angle + Math.PI / 4) * length);
    }

    private static Pen RoundedPen(Color color, float width) => new(color, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };

    private static void Add(ImageList images, string key, UiIconKind kind) => images.Images.Add(key, Create(kind, 16));

    private static void AddShell(ImageList images, string key, string fileName) =>
        images.Images.Add(key, ShellImage(fileName, 16) ?? Create(UiIconKind.File, 16));

    private static Image? ShellImage(string path, int size)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;
        try
        {
            using var icon = (Icon)Icon.FromHandle(info.IconHandle).Clone();
            using var source = icon.ToBitmap();
            return new Bitmap(source, new Size(size, size));
        }
        finally { DestroyIcon(info.IconHandle); }
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

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
