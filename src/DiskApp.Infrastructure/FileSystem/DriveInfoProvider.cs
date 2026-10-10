using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using DiskApp.Core.FileSystem;
using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Infrastructure.FileSystem;

/// <summary>
/// Определяет тип накопителя и физический диск по пути. В Windows для локальных томов спрашивает у драйвера
/// задержку позиционирования (<c>StorageDeviceSeekPenaltyProperty</c>: SSD или HDD) и номер диска тома
/// (<c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c>); права администратора не нужны. Результат кэшируется по корню тома.
/// </summary>
public sealed class DriveInfoProvider : IDriveInfoProvider
{
    private readonly ConcurrentDictionary<string, DriveDescription> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public DriveDescription Describe(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) && !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var server = fullPath[2..].Split('\\', 2)[0].ToLowerInvariant();
            return new DriveDescription("net:" + server, DriveMedia.Network);
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return new DriveDescription("path:" + fullPath, DriveMedia.Unknown);
        }

        return _cache.GetOrAdd(root, Detect);
    }

    private static DriveDescription Detect(string root)
    {
        var volumeKey = "vol:" + root.ToUpperInvariant();
        DriveType type;
        try
        {
            type = new DriveInfo(root).DriveType;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new DriveDescription(volumeKey, DriveMedia.Unknown);
        }

        switch (type)
        {
            case DriveType.Network:
                return new DriveDescription("net:" + root.ToUpperInvariant(), DriveMedia.Network);
            case DriveType.Removable or DriveType.CDRom:
                return new DriveDescription(volumeKey, DriveMedia.Removable);
            case DriveType.Fixed when OperatingSystem.IsWindows():
                return DetectWindowsFixed(root, volumeKey);
            default:
                return new DriveDescription(volumeKey, DriveMedia.Unknown);
        }
    }

    [SupportedOSPlatform("windows")]
    private static DriveDescription DetectWindowsFixed(string root, string volumeKey)
    {
        // «C:\» → «\\.\C:»: устройство тома; доступ 0 достаточен для запросов свойств без прав администратора.
        var device = @"\\.\" + root.TrimEnd('\\');
        using var handle = NativeMethods.CreateFile(device, 0, NativeMethods.FileShareAll, IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return new DriveDescription(volumeKey, DriveMedia.Unknown);
        }

        var media = DriveMedia.Unknown;
        var query = new NativeMethods.StoragePropertyQuery
        {
            PropertyId = NativeMethods.StorageDeviceSeekPenaltyProperty,
            QueryType = NativeMethods.PropertyStandardQuery,
        };

        if (NativeMethods.QueryStorageProperty(
                handle,
                NativeMethods.IoctlStorageQueryProperty,
                ref query,
                (uint)Marshal.SizeOf<NativeMethods.StoragePropertyQuery>(),
                out var penalty,
                (uint)Marshal.SizeOf<NativeMethods.DeviceSeekPenaltyDescriptor>(),
                out _,
                IntPtr.Zero))
        {
            media = penalty.IncursSeekPenalty != 0 ? DriveMedia.Rotational : DriveMedia.SolidState;
        }

        var key = volumeKey;
        if (NativeMethods.QueryVolumeDiskExtents(
                handle,
                NativeMethods.IoctlVolumeGetVolumeDiskExtents,
                IntPtr.Zero,
                0,
                out var extents,
                (uint)Marshal.SizeOf<NativeMethods.VolumeDiskExtents>(),
                out _,
                IntPtr.Zero)
            && extents.NumberOfDiskExtents == 1)
        {
            key = "disk:" + extents.DiskNumber.ToString(CultureInfo.InvariantCulture);
        }

        return new DriveDescription(key, media);
    }
}
