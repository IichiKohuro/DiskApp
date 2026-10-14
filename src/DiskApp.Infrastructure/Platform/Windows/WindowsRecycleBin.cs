using System.Buffers.Binary;
using System.Text;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Results;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Корзина Windows для истории файлов (PLAN.md, этап 20.2): у каждого тома — <c>X:\$Recycle.Bin\&lt;SID пользователя&gt;</c>,
/// где удалённый лежит парой файлов: <c>$R…</c> — сам файл или папка, <c>$I…</c> с тем же хвостом — запись о нём: размер,
/// время удаления и прежний путь. Формат записи: 8 байт версии (1 — Windows Vista–8.1, путь фиксированной длины 260
/// символов; 2 — Windows 10 и новее, длина пути в символах перед путём), 8 байт размера, 8 байт FILETIME, путь в UTF-16.
/// «Восстановить» делает то же, что Проводник: переносит <c>$R…</c> на прежнее место и удаляет запись.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const int HeaderSize = 24;
    private const int LegacyPathChars = 260;

    private readonly Func<string, IReadOnlyList<string>> _binFolders;

    /// <summary>Корзина текущего пользователя.</summary>
    public WindowsRecycleBin()
        : this(CurrentUserBins)
    {
    }

    /// <summary>Корзина с заданными папками (тесты).</summary>
    /// <param name="binFolders">Папки корзины для пути: по корню тома.</param>
    public WindowsRecycleBin(Func<string, IReadOnlyList<string>> binFolders)
    {
        _binFolders = binFolders ?? throw new ArgumentNullException(nameof(binFolders));
    }

    /// <inheritdoc />
    public RecycledItem? Find(string originalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);

        RecycledItem? latest = null;
        foreach (var folder in _binFolders(originalPath))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var record in Directory.EnumerateFiles(folder, "$I*"))
            {
                if (Read(record) is not { } item || !string.Equals(item.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stored = Path.Combine(folder, "$R" + Path.GetFileName(record)[2..]);
                if ((File.Exists(stored) || Directory.Exists(stored)) && (latest is null || item.DeletedUtc > latest.DeletedUtc))
                {
                    latest = item with { StoredPath = stored };
                }
            }
        }

        return latest;
    }

    /// <inheritdoc />
    public Result Restore(string storedPath, string originalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(storedPath);
        ArgumentException.ThrowIfNullOrEmpty(originalPath);

        try
        {
            if (Directory.Exists(storedPath))
            {
                Directory.Move(storedPath, originalPath);
            }
            else if (File.Exists(storedPath))
            {
                File.Move(storedPath, originalPath);
            }
            else
            {
                return Result.Fail(new DomainError("history.not_in_recycle_bin", Core.Localization.L.T("History.NotInRecycleBin")));
            }

            var name = Path.GetFileName(storedPath);
            var record = Path.Combine(Path.GetDirectoryName(storedPath)!, "$I" + name[2..]);
            if (File.Exists(record))
            {
                File.Delete(record);
            }

            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(new DomainError("history.restore_failed", exception.Message));
        }
    }

    /// <summary>Читает запись <c>$I…</c>; <c>null</c> — не запись корзины.</summary>
    internal static RecycledItem? Read(string recordPath)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(recordPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (data.Length < HeaderSize)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadInt64LittleEndian(data);
        var size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        var deleted = DateTimeOffset.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16))).ToUniversalTime();
        string path;
        if (version == 1 && data.Length >= HeaderSize + (LegacyPathChars * 2))
        {
            path = Encoding.Unicode.GetString(data, HeaderSize, LegacyPathChars * 2);
        }
        else if (version == 2 && data.Length >= HeaderSize + 4)
        {
            var chars = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(HeaderSize));
            if (chars <= 0 || data.Length < HeaderSize + 4 + (chars * 2))
            {
                return null;
            }

            path = Encoding.Unicode.GetString(data, HeaderSize + 4, chars * 2);
        }
        else
        {
            return null;
        }

        var end = path.IndexOf('\0', StringComparison.Ordinal);
        return new RecycledItem(end >= 0 ? path[..end] : path, string.Empty, deleted, size);
    }

    // Корзина пользователя на томе пути: «X:\$Recycle.Bin\<SID>».
    private static IReadOnlyList<string> CurrentUserBins(string path)
    {
        if (!OperatingSystem.IsWindows() || Path.GetPathRoot(path) is not { Length: > 0 } root)
        {
            return [];
        }

        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        return sid is null ? [] : [Path.Combine(root, "$Recycle.Bin", sid)];
    }
}
