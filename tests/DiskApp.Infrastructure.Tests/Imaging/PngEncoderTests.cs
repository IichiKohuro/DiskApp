using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using DiskApp.Infrastructure.Imaging;

namespace DiskApp.Infrastructure.Tests.Imaging;

/// <summary>
/// Кодировщик PNG для иконок (PLAN.md, этап 7.4) и кэш с вытеснением.
/// </summary>
public sealed class PngEncoderTests
{
    [Fact]
    public void Crc32_StandardCheckValue_Matches()
    {
        // Act & Assert
        PngEncoder.Crc32(Encoding.ASCII.GetBytes("123456789")).ShouldBe(0xCBF43926u);
    }

    [Fact]
    public void EncodeBgra_TwoPixels_WritesValidPngWithRgbaRows()
    {
        // Arrange: синий непрозрачный и красный полупрозрачный пиксель.
        byte[] bgra = [255, 0, 0, 255, 0, 0, 255, 128];

        // Act
        var png = PngEncoder.EncodeBgra(bgra, 2, 1);

        // Assert
        png.AsSpan(0, 8).ToArray().ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Encoding.ASCII.GetString(png, 12, 4).ShouldBe("IHDR");
        BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)).ShouldBe(2);
        BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)).ShouldBe(1);
        png[24].ShouldBe((byte)8);
        png[25].ShouldBe((byte)6);
        VerifyChunkCrcs(png);
        ReadRows(png).ShouldBe(new byte[] { 0, 0, 0, 255, 255, 255, 0, 0, 128 });
    }

    [Fact]
    public void EncodeBgra_WrongBufferLength_Throws()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => PngEncoder.EncodeBgra(new byte[7], 2, 1));
    }

    [Fact]
    public void LruCache_OverCapacity_EvictsLeastRecentlyUsed()
    {
        // Arrange
        var cache = new LruCache<string, string?>(2);
        cache.Set("a", "A");
        cache.Set("b", null);
        cache.TryGet("a", out _);

        // Act
        cache.Set("c", "C");

        // Assert
        cache.TryGet("b", out _).ShouldBeFalse("давно не использованная запись вытеснена");
        cache.TryGet("a", out var a).ShouldBeTrue();
        a.ShouldBe("A");
        cache.TryGet("c", out _).ShouldBeTrue();
        cache.Count.ShouldBe(2);
    }

    [Fact]
    public void LruCache_NullValue_IsCached()
    {
        // Arrange
        var cache = new LruCache<int, string?>(4);

        // Act
        cache.Set(1, null);

        // Assert
        cache.TryGet(1, out var value).ShouldBeTrue("«изображения нет» тоже ответ");
        value.ShouldBeNull();
    }

    private static void VerifyChunkCrcs(byte[] png)
    {
        var position = 8;
        while (position < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length));
            PngEncoder.Crc32(png.AsSpan(position + 4, length + 4)).ShouldBe(crc);
            position += 12 + length;
        }

        position.ShouldBe(png.Length);
    }

    private static byte[] ReadRows(byte[] png)
    {
        var position = 8;
        while (position < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            if (Encoding.ASCII.GetString(png, position + 4, 4) == "IDAT")
            {
                using var zlib = new ZLibStream(new MemoryStream(png, position + 8, length), CompressionMode.Decompress);
                using var raw = new MemoryStream();
                zlib.CopyTo(raw);
                return raw.ToArray();
            }

            position += 12 + length;
        }

        throw new InvalidOperationException("Нет блока IDAT.");
    }
}
