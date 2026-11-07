using System.Text;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ObjectContentEncodingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("identity")]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    [InlineData("gzip, br")]
    public void EncodedTextRoundTripsAndPreservesBytes(string? encoding)
    {
        var bytes = Encoding.UTF8.GetBytes("hello 你好\n");
        var encoded = ObjectContentEncoding.Encode(bytes, encoding);
        Assert.Equal(bytes, ObjectContentEncoding.Decode(encoded, encoding, 1024));
        Assert.Throws<InvalidDataException>(() => ObjectContentEncoding.Decode(encoded, encoding, 1));
    }

    [Fact]
    public void HighCompressionCannotExpandPastTheLimit()
    {
        var encoded = ObjectContentEncoding.Encode(new byte[1024 * 1024], "gzip");
        Assert.True(encoded.Length < 2048);
        Assert.Throws<InvalidDataException>(() => ObjectContentEncoding.Decode(encoded, "gzip", 2048));
    }

    [Fact]
    public void UnknownEncodingNeverFallsBackToUploadingTheWrongRepresentation()
    {
        Assert.Throws<NotSupportedException>(() => ObjectContentEncoding.Decode([1], "unknown", 1024));
        Assert.Throws<NotSupportedException>(() => ObjectContentEncoding.Encode([1], "unknown"));
    }
}
