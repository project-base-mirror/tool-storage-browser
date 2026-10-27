using System.Text;
using System.Runtime.ExceptionServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ObjectPreviewTests
{
    [Theory]
    [InlineData("report.JSON", 2097152L)]
    [InlineData("report.csv", 2097152L)]
    [InlineData("page.html", 2097152L)]
    [InlineData("photo.JPG", 8388608L)]
    [InlineData("payload.exe", 0L)]
    [InlineData("image.svg", 0L)]
    [InlineData("animated.gif", 0L)]
    [InlineData("dataset.parquet", 0L)]
    public void TypesHaveBoundedReadOnlyPreviewLimits(string key, long limit) =>
        Assert.Equal(limit, ObjectPreviewPolicy.MaximumBytes(key));

    [Fact]
    public void TextDecoderRejectsBinaryAndInvalidUtf8()
    {
        Assert.Equal("hello", ObjectPreviewPolicy.DecodeText(Encoding.UTF8.GetBytes("hello")));
        Assert.Throws<InvalidDataException>(() => ObjectPreviewPolicy.DecodeText([65, 0, 66]));
        Assert.Throws<DecoderFallbackException>(() => ObjectPreviewPolicy.DecodeText([0xff, 0xff]));
        Assert.Equal("你好", ObjectPreviewPolicy.DecodeText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("你好")).ToArray()));
    }

    [Fact]
    public void ExternalEditorNeverUsesDownloadedFileAssociationOrShell()
    {
        var info = ObjectPreviewPolicy.EditorStartInfo("C:\\temp\\literal & script.js.txt");
        Assert.False(info.UseShellExecute);
        Assert.Equal("notepad.exe", Path.GetFileName(info.FileName));
        Assert.Equal("C:\\temp\\literal & script.js.txt", Assert.Single(info.ArgumentList));
        Assert.Empty(info.Arguments);
    }

    [Fact]
    public void EditingRejectsChangedVersionEvenWithSameEtag()
    {
        var original = new ObjectProperties("bucket", "key", 3, null, "etag", "text/plain", null, "v1", new Dictionary<string, string>());
        MainForm.EnsurePreviewIdentityUnchanged(original, original);
        Assert.Throws<InvalidOperationException>(() => MainForm.EnsurePreviewIdentityUnchanged(original, original with { ETag = "different" }));
        Assert.Throws<InvalidOperationException>(() => MainForm.EnsurePreviewIdentityUnchanged(original, original with { VersionId = "v2" }));
        Assert.Throws<InvalidOperationException>(() => MainForm.EnsurePreviewIdentityUnchanged(original with { ETag = null }, original));
        Assert.Throws<InvalidOperationException>(() => MainForm.EnsurePreviewIdentityUnchanged(original, original with { CacheControl = "changed" }));
        Assert.Throws<InvalidOperationException>(() => MainForm.EnsurePreviewIdentityUnchanged(original, original with { Metadata = new Dictionary<string, string> { ["new"] = "value" } }));
        // The profile's default for new uploads must not change an existing STANDARD object.
        Assert.Equal("STANDARD", ObjectPreviewPolicy.ExistingStorageClass(null));
        Assert.Equal("STANDARD", ObjectPreviewPolicy.ExistingStorageClass(""));
        Assert.Equal("GLACIER", ObjectPreviewPolicy.ExistingStorageClass("GLACIER"));
    }
    [Fact]
    public void ImageHeadersRejectOversizedOrAnimatedContentBeforeNativeDecode()
    {
        var png = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 100_000);
        var completeHeader = new byte[45];
        png.CopyTo(completeHeader, 0);
        "IEND"u8.CopyTo(completeHeader.AsSpan(37));
        Assert.Contains("像素", Assert.Throws<InvalidDataException>(() => ObjectPreviewPolicy.ValidateImageBeforeDecode("huge.png", completeHeader)).Message);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 1);
        var animatedPng = new byte[53];
        png.CopyTo(animatedPng, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(animatedPng.AsSpan(33, 4), 8);
        "acTL"u8.CopyTo(animatedPng.AsSpan(37));
        Assert.Throws<InvalidDataException>(() => ObjectPreviewPolicy.ValidateImageBeforeDecode("animation.png", animatedPng));
        var duplicateHeader = new byte[70];
        png.CopyTo(duplicateHeader, 0);
        png.AsSpan(8, 25).CopyTo(duplicateHeader.AsSpan(33));
        "IEND"u8.CopyTo(duplicateHeader.AsSpan(62));
        Assert.Throws<InvalidDataException>(() => ObjectPreviewPolicy.ValidateImageBeforeDecode("duplicate.png", duplicateHeader));
        // Even a tiny GIF with several frame markers never reaches GDI+.
        Assert.Throws<InvalidDataException>(() => ObjectPreviewPolicy.ValidateImageBeforeDecode("animation.gif", Encoding.ASCII.GetBytes("GIF89a,frame,frame;")));
        Assert.Throws<InvalidDataException>(() => ObjectPreviewPolicy.ValidateImageBeforeDecode("broken.jpg", [0xff, 0xd8, 0xff]));
    }

    [Fact]
    public void ProtectedObjectsAreReadOnlyAndChangedProtectionBlocksUpload()
    {
        var normal = new ObjectProperties("bucket", "key", 3, null, "etag", "text/plain", null, "v1", new Dictionary<string, string>());
        Assert.Null(ObjectPreviewPolicy.ProtectedObjectReadOnlyReason(normal));
        var sse = normal with { ServerSideEncryptionMethod = "AES256" };
        Assert.Null(ObjectPreviewPolicy.ProtectedObjectReadOnlyReason(sse));
        Assert.True(ObjectPreviewPolicy.RequiresSseS3Encryption(sse));
        Assert.False(ObjectPreviewPolicy.RequiresSseS3Encryption(normal));
        Assert.Null(ObjectPreviewPolicy.EditingReadOnlyReason(S3ServiceType.AmazonS3, sse));
        Assert.Null(ObjectPreviewPolicy.EditingReadOnlyReason(S3ServiceType.MinIO, sse));
        Assert.NotNull(ObjectPreviewPolicy.EditingReadOnlyReason(S3ServiceType.CloudflareR2, sse));
        var protectedObjects = new[]
        {
            normal with { ServerSideEncryptionMethod = "aws:kms" },
            normal with { ServerSideEncryptionMethod = "aws:kms:dsse" },
            normal with { EncryptionKeyId = "key-id" },
            normal with { CustomerEncryptionMethod = "AES256" },
            normal with { ObjectLockMode = "GOVERNANCE" },
            normal with { ObjectLockMode = "COMPLIANCE" },
            normal with { ObjectLockLegalHoldStatus = "ON" }
        };
        foreach (var item in protectedObjects)
        {
            Assert.NotNull(ObjectPreviewPolicy.ProtectedObjectReadOnlyReason(item));
            Assert.Throws<InvalidOperationException>(() => MainForm.EnsurePreviewIdentityUnchanged(normal, item));
        }
        Assert.Null(ObjectPreviewPolicy.ProtectedObjectReadOnlyReason(normal with { ObjectLockLegalHoldStatus = "OFF" }));
    }

    [Fact]
    public void ChangedOrUnconfirmedEditsAreRetainedButUploadedOrUnchangedContentCanBeCleaned()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.False(ObjectPreviewPolicy.ShouldRetainEditedFile(path, [1, 2, 3]));
            Assert.True(ObjectPreviewPolicy.ShouldRetainEditedFile(path, [1, 2, 4]));
            Assert.True(ObjectPreviewPolicy.ShouldRetainEditedFile(path, [1]));
            Assert.True(ObjectPreviewPolicy.ShouldRetainEditedFile(path, null));
            // Passing the exact bytes acknowledged by upload is the only changed-content cleanup case.
            File.WriteAllBytes(path, [4, 5]);
            Assert.False(ObjectPreviewPolicy.ShouldRetainEditedFile(path, [4, 5]));
            File.Delete(path);
            Assert.False(ObjectPreviewPolicy.ShouldRetainEditedFile(path, [4, 5]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HtmlRemainsPlainTextAndCsvTableIsBounded()
    {
        RunSta(() =>
        {
            using var html = NewDialog();
            const string script = "<script>alert('never execute')</script>";
            html.ShowContent(new ObjectPreviewContent("page.html", Encoding.UTF8.GetBytes(script), false, ""));
            var text = Assert.Single(Descendants(html).OfType<TextBox>());
            Assert.True(text.ReadOnly);
            Assert.Equal(script, text.Text);
            Assert.Empty(Descendants(html).OfType<WebBrowser>());
            using var csv = NewDialog();
            var row = string.Join(",", Enumerable.Range(0, 55).Select(index => index == 0 ? "\"a,b\"" : index.ToString()));
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Repeat(row, 250)));
            csv.ShowContent(new ObjectPreviewContent("data.csv", bytes, false, ""));
            var grid = Assert.Single(Descendants(csv).OfType<DataGridView>());
            Assert.Equal(200, grid.Rows.Count);
            Assert.Equal(50, grid.Columns.Count);
            Assert.Equal("a,b", grid.Rows[0].Cells[0].Value);
            Assert.True(grid.ReadOnly);
        });
    }

    private static ObjectPreviewDialog NewDialog() => new("test",
        _ => Task.FromResult(new ObjectPreviewContent("test.txt", [], false, "")), _ => Task.CompletedTask);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
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
