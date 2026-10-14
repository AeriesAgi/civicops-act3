using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class CrewPhotoTests
{
    [Fact]
    public void ServerAlwaysRemovesPngMetadataWithoutAnyImageIdentification()
    {
        var bytes = Png(); Assert.Contains("synthetic-gps-metadata", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        var sanitized = CrewPhotoSanitizer.Strip(bytes, "image/png"); Assert.DoesNotContain("synthetic-gps-metadata", Encoding.ASCII.GetString(sanitized), StringComparison.Ordinal);
        Assert.Equal(sanitized, CrewPhotoSanitizer.Strip(sanitized, "image/png")); Assert.Contains("IDAT", Encoding.ASCII.GetString(sanitized), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("mismatch")] [InlineData("truncated")] [InlineData("crc")] [InlineData("oversized")] [InlineData("invalid-pixels")]
    public void MalformedMismatchedOversizedOrIncompletePhotoCannotBeStored(string invalid)
    {
        var bytes = Png(); var mime = "image/png";
        if (invalid == "mismatch") mime = "image/jpeg";
        if (invalid == "truncated") bytes = bytes[..^5];
        if (invalid == "crc") bytes[30] ^= 1;
        if (invalid == "oversized") bytes = new byte[CrewPhotoSanitizer.MaximumBytes + 1];
        if (invalid == "invalid-pixels") bytes = Encoding.ASCII.GetBytes("synthetic non-image payload that is not a JPEG or PNG");
        Assert.Throws<CrewRequestException>(() => CrewPhotoSanitizer.Strip(bytes, mime));
    }
    [Theory]
    [InlineData(5000, 5001)] [InlineData(16384, 16384)] [InlineData(17000, 1)]
    public void HighlyCompressedPhotosCannotExceedPixelBounds(int width, int height) => Assert.Throws<CrewRequestException>(() => CrewPhotoSanitizer.Strip(Png(width, height), "image/png"));
    internal static byte[] Png(int width = 1, int height = 1)
    {
        using var result = new MemoryStream(); result.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height); Chunk(result, "IHDR", header);
        Chunk(result, "tEXt", Encoding.ASCII.GetBytes("Description\0synthetic-gps-metadata")); Chunk(result, "eXIf", Encoding.ASCII.GetBytes("synthetic-gps-metadata"));
        using var pixels = new MemoryStream(); using (var compressed = new ZLibStream(pixels, CompressionLevel.SmallestSize, true)) compressed.Write([0, 255, 0, 0, 255]);
        Chunk(result, "IDAT", pixels.ToArray()); Chunk(result, "IEND", []); return result.ToArray();
    }
    private static void Chunk(Stream output, string kind, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length); output.Write(buffer);
        var name = Encoding.ASCII.GetBytes(kind); output.Write(name); output.Write(data); uint crc = 0xffffffff;
        foreach (var value in name.Concat(data)) { crc ^= value; for (var index = 0; index < 8; index++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        BinaryPrimitives.WriteUInt32BigEndian(buffer, ~crc); output.Write(buffer);
    }
}
