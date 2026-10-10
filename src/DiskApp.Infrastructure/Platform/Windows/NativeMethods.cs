using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Вызовы Win32 для индексации: идентичность файла (том и номер), тип накопителя и физический диск тома,
/// фоновый режим потока. Только Windows; вызывающий код охраняет вызовы <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const uint FileReadAttributes = 0x80;
    internal const uint FileShareAll = 0x1 | 0x2 | 0x4;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    internal const int StorageDeviceSeekPenaltyProperty = 7;
    internal const int PropertyStandardQuery = 0;
    internal const int ThreadModeBackgroundBegin = 0x00010000;
    internal const int ThreadModeBackgroundEnd = 0x00020000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryStorageProperty(
        SafeFileHandle device,
        uint ioControlCode,
        ref StoragePropertyQuery query,
        uint querySize,
        out DeviceSeekPenaltyDescriptor result,
        uint resultSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryVolumeDiskExtents(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        out VolumeDiskExtents result,
        uint resultSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadPriority(IntPtr thread, int priority);

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>; <c>FILETIME</c> разложены на пары <c>DWORD</c>, чтобы не менять выравнивание.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary><c>STORAGE_PROPERTY_QUERY</c> с одним байтом дополнительных параметров.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    /// <summary><c>DEVICE_SEEK_PENALTY_DESCRIPTOR</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    /// <summary><c>VOLUME_DISK_EXTENTS</c> с одним экстентом; тома на нескольких дисках дают ошибку, и ключ берётся по тому.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VolumeDiskExtents
    {
        public uint NumberOfDiskExtents;
        public uint Padding;
        public uint DiskNumber;
        public uint ExtentPadding;
        public long StartingOffset;
        public long ExtentLength;
    }
}
