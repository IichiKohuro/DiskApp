using System.Buffers.Binary;
using System.IO.Compression;

namespace DiskApp.Infrastructure.Imaging;

/// <summary>
/// Минимальный кодировщик PNG для иконок и миниатюр: RGBA 8 бит, без чересстрочности, сжатие zlib из BCL.
/// Своей библиотеки изображений в проекте нет, а растры оболочки приходят как пиксели BGRA.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>
    /// Кодирует пиксели BGRA (прямая, не премультиплицированная альфа; строки сверху вниз) в PNG.
    /// </summary>
    /// <param name="bgra">Пиксели, по 4 байта: синий, зелёный, красный, альфа.</param>
    /// <param name="width">Ширина.</param>
    /// <param name="height">Высота.</param>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException($"Ожидалось {width * height * 4} байт пикселей, получено {bgra.Length}.", nameof(bgra));
        }

        // Каждая строка: байт фильтра (0 — без фильтра) и пиксели в порядке RGBA.
        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var source = bgra.Slice(y * stride, stride);
            var row = raw.AsSpan((y * (stride + 1)) + 1, stride);
            for (var x = 0; x < stride; x += 4)
            {
                row[x] = source[x + 2];
                row[x + 1] = source[x + 1];
                row[x + 2] = source[x];
                row[x + 3] = source[x + 3];
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // бит на канал
        header[9] = 6; // RGBA
        header[10] = 0; // сжатие deflate
        header[11] = 0; // фильтры по строкам
        header[12] = 0; // без чересстрочности

        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    /// <summary>CRC-32 (ISO-HDLC), которой PNG подписывает блоки.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(type);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(data, Crc32(type)));
        stream.Write(number);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
