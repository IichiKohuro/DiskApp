using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Вызовы оболочки и GDI для иконок и миниатюр (PLAN.md, этап 7.4): системный список изображений
/// (<c>SHGetFileInfo</c> + <c>SHGetImageList</c>), <c>IShellItemImageFactory</c> и чтение пикселей <c>HBITMAP</c>.
/// COM-интерфейсы вызываются через таблицу методов, без обёрток среды.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class IconNative
{
    public const uint FileAttributeDirectory = 0x10;
    public const uint FileAttributeNormal = 0x80;

    public const uint ShgfiSysIconIndex = 0x4000;
    public const uint ShgfiUseFileAttributes = 0x10;

    public const int ShilLarge = 0;
    public const int ShilSmall = 1;
    public const int ShilExtraLarge = 2;
    public const int ShilJumbo = 4;

    public const uint IldTransparent = 0x1;

    public const int SiigbfIconOnly = 0x4;
    public const int SiigbfThumbnailOnly = 0x8;
    public const int SiigbfBiggerSizeOk = 0x1;

    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    public static readonly Guid ImageListId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    public static readonly Guid ShellItemImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    [StructLayout(LayoutKind.Sequential)]
    public struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public fixed uint Colors[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint ShGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetImageList")]
    public static partial int ShGetImageList(int list, in Guid id, out nint imageList);

    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ShCreateItemFromParsingName(string path, nint bindContext, in Guid id, out nint item);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", EntryPoint = "GetIconInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(nint handle, int size, out BitmapHeader bitmap);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte* bits, ref BitmapInfo info, uint usage);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    /// <summary><c>IUnknown::Release</c>.</summary>
    public static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            var vtable = *(nint**)unknown;
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(unknown);
        }
    }

    /// <summary><c>IImageList::GetIcon</c> — метод 10 таблицы.</summary>
    public static int ImageListGetIcon(nint imageList, int index, uint flags, out nint icon)
    {
        var vtable = *(nint**)imageList;
        nint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)vtable[10])(imageList, index, flags, &result);
        icon = result;
        return hr;
    }

    /// <summary><c>IShellItemImageFactory::GetImage</c> — метод 3 таблицы.</summary>
    public static int ImageFactoryGetImage(nint factory, NativeSize size, int flags, out nint bitmap)
    {
        var vtable = *(nint**)factory;
        nint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, NativeSize, int, nint*, int>)vtable[3])(factory, size, flags, &result);
        bitmap = result;
        return hr;
    }

    /// <summary>
    /// Пиксели <c>HBITMAP</c> как BGRA 32 бита, строки сверху вниз; <c>null</c>, если GDI не отдал их.
    /// </summary>
    public static byte[]? ReadPixels(nint bitmap, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (GetObject(bitmap, sizeof(BitmapHeader), out var header) == 0 || header.Width <= 0 || header.Height == 0)
        {
            return null;
        }

        width = header.Width;
        height = Math.Abs(header.Height);
        var info = new BitmapInfo
        {
            Size = 40,
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
        };
        var pixels = new byte[width * height * 4];
        var dc = CreateCompatibleDC(0);
        try
        {
            fixed (byte* target = pixels)
            {
                return GetDIBits(dc, bitmap, 0, (uint)height, target, ref info, DibRgbColors) == height ? pixels : null;
            }
        }
        finally
        {
            DeleteDC(dc);
        }
    }
}
