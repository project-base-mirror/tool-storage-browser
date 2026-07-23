namespace S3Explorer.App;

internal static class UiIcons
{
    public static Image Create(string glyph)
    {
        var bitmap = new Bitmap(20, 20, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var font = new Font("Segoe UI Symbol", 10, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(SystemColors.ControlText);
        var size = graphics.MeasureString(glyph, font);
        graphics.DrawString(glyph, font, brush, (20 - size.Width) / 2f, (20 - size.Height) / 2f);
        return bitmap;
    }
}
