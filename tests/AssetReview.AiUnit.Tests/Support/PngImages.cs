using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AssetReview.AiUnit.Tests;

internal readonly record struct Rgb(byte R, byte G, byte B);

internal static class PngImages
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(string path, int width, int height, Func<int, int, Rgb> pixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(output, "IHDR", Header(width, height));

        var raw = new byte[(width * 3 + 1) * height];
        var index = 0;
        for (var y = 0; y < height; y++)
        {
            raw[index++] = 0;
            for (var x = 0; x < width; x++)
            {
                var px = pixel(x, y);
                raw[index++] = px.R;
                raw[index++] = px.G;
                raw[index++] = px.B;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        File.WriteAllBytes(path, output.ToArray());
    }

    public static void WriteSolid(string path, int width, int height, Rgb color) =>
        Write(path, width, height, (_, _) => color);

    public static void WritePreview(string path, int width, int height, Rgb color) =>
        Write(path, width, height, (x, y) =>
        {
            if (x < 8 || y < 8 || x >= width - 8 || y >= height - 8)
                return new Rgb(255, 255, 255);
            if (((x / 16) + (y / 16)) % 2 == 0)
                return color;
            return new Rgb((byte)(color.R / 2), (byte)(color.G / 2), (byte)(color.B / 2));
        });

    private static byte[] Header(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data, width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), height);
        data[8] = 8;
        data[9] = 2;
        return data;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}

internal static class SvgImages
{
    public static void Write(string path, string label, string fill)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var text = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="160" height="160" viewBox="0 0 160 160">
              <rect width="160" height="160" fill="{fill}"/>
              <circle cx="80" cy="68" r="36" fill="#ffffff"/>
              <text x="80" y="132" text-anchor="middle" font-family="sans-serif" font-size="22" fill="#ffffff">{System.Net.WebUtility.HtmlEncode(label)}</text>
            </svg>
            """;
        File.WriteAllText(path, text);
    }
}
