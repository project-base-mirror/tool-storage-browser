using System.IO.Compression;

namespace S3Explorer.Core;

// Decode only explicitly supported HTTP encodings and bound every decoded layer.
public static class ObjectContentEncoding
{
    public static byte[] Decode(byte[] bytes, string? contentEncoding, long limit)
    {
        foreach (var encoding in ContentEncodings(contentEncoding).Reverse())
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var decoded = CompressionStream(input, encoding, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = decoded.Read(buffer)) > 0)
            {
                if (output.Length + count > limit)
                    throw new InvalidDataException("解压后的对象超过预览上限，不会继续展开。");
                output.Write(buffer, 0, count);
            }
            bytes = output.ToArray();
        }
        if (bytes.LongLength > limit) throw new InvalidDataException("对象超过预览上限。");
        return bytes;
    }

    public static byte[] Encode(byte[] bytes, string? contentEncoding)
    {
        foreach (var encoding in ContentEncodings(contentEncoding))
        {
            using var output = new MemoryStream();
            using (var encoded = CompressionStream(output, encoding, CompressionMode.Compress))
                encoded.Write(bytes);
            bytes = output.ToArray();
        }
        return bytes;
    }

    private static string[] ContentEncodings(string? value)
    {
        var encodings = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)).Select(value => value.ToLowerInvariant()).ToArray();
        if (encodings.Any(value => value is not ("gzip" or "br" or "deflate")))
            throw new NotSupportedException("此 Content-Encoding 不支持安全解码与回传；请下载后使用对应工具编辑。");
        return encodings;
    }

    private static Stream CompressionStream(Stream stream, string encoding, CompressionMode mode) => encoding switch
    {
        "gzip" => new GZipStream(stream, mode, leaveOpen: true),
        "br" => new BrotliStream(stream, mode, leaveOpen: true),
        // HTTP deflate is the zlib wrapper, not an unwrapped DEFLATE stream.
        "deflate" => new ZLibStream(stream, mode, leaveOpen: true),
        _ => throw new NotSupportedException("不支持的内容编码。")
    };

}
