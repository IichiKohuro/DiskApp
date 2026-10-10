using System.Globalization;
using System.IO.Enumeration;
using System.Security;

using DiskApp.Core.FileSystem;
using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Infrastructure.FileSystem;

/// <summary>
/// Листинг файловой системы через <see cref="FileSystemEnumerable{TResult}"/>: одна папка за раз, без рекурсии и фильтрации,
/// метаданные берутся из данных перечисления без отдельного запроса к каждому файлу. Содержимое файлов не читается,
/// поэтому облачные заглушки не скачиваются.
/// </summary>
public sealed class FileSystemLister : IFileSystemLister
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>Единственный экземпляр: состояния нет.</summary>
    public static FileSystemLister Instance { get; } = new();

    /// <inheritdoc />
    public DirectoryListing List(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var items = new List<FileSystemItem>();
        try
        {
            var enumerable = new FileSystemEnumerable<FileSystemItem>(fullPath, Transform, Options);
            foreach (var item in enumerable)
            {
                items.Add(item);
            }

            return new DirectoryListing(items, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new DirectoryListing(items, exception.Message);
        }
    }

    /// <inheritdoc />
    public StatResult Stat(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                var directory = new DirectoryInfo(fullPath);
                return new StatResult(
                    new FileSystemItem(directory.Name, true, 0, ToUtc(directory.CreationTimeUtc), ToUtc(directory.LastWriteTimeUtc), attributes),
                    null);
            }

            var file = new FileInfo(fullPath);
            return new StatResult(
                new FileSystemItem(file.Name, false, file.Length, ToUtc(file.CreationTimeUtc), ToUtc(file.LastWriteTimeUtc), attributes),
                null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return StatResult.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new StatResult(null, exception.Message);
        }
    }

    /// <inheritdoc />
    public bool DirectoryExists(string fullPath) => Directory.Exists(fullPath);

    /// <inheritdoc />
    public string? GetDirectoryIdentity(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (OperatingSystem.IsWindows())
        {
            return GetWindowsIdentity(fullPath);
        }

        try
        {
            var target = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileSystemItem Transform(ref FileSystemEntry entry)
        => new(
            entry.FileName.ToString(),
            entry.IsDirectory,
            entry.IsDirectory ? 0 : entry.Length,
            entry.CreationTimeUtc,
            entry.LastWriteTimeUtc,
            entry.Attributes);

    private static DateTimeOffset ToUtc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? GetWindowsIdentity(string fullPath)
    {
        // Без FILE_FLAG_OPEN_REPARSE_POINT система идёт по ссылке, и мы получаем идентичность цели.
        using var handle = NativeMethods.CreateFile(
            fullPath.Length > 259 && !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + fullPath : fullPath,
            NativeMethods.FileReadAttributes,
            NativeMethods.FileShareAll,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid || !NativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}");
    }
}
