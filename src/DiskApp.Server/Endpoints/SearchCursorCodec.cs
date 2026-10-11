using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;

using DiskApp.Core.Search;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Курсор страницы поиска в API — непрозрачная строка: оценка и идентификатор последней записи, 16 байт в Base64Url.
/// </summary>
internal static class SearchCursorCodec
{
    private const int Size = sizeof(double) + sizeof(long);

    public static string Encode(SearchCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[Size];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, cursor.Score);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[sizeof(double)..], cursor.EntryId);
        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryDecode(string? text, [NotNullWhen(true)] out SearchCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(text) || text.Length > 32)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Size];
        if (Base64Url.DecodeFromChars(text, bytes, out var consumed, out var written) != System.Buffers.OperationStatus.Done
            || consumed != text.Length
            || written != Size)
        {
            return false;
        }

        var score = BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        if (!double.IsFinite(score))
        {
            return false;
        }

        cursor = new SearchCursor(score, BinaryPrimitives.ReadInt64LittleEndian(bytes[sizeof(double)..]));
        return true;
    }
}
