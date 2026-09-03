using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenGepa.Services;

/// <summary>Windows Shell が公開するアイコンを、一時的に WPF イメージへ変換します。</summary>
/// <remarks>ランタイムの特別タブ用であり、icon ディレクトリや JSON へは保存しません。</remarks>
public static class ShellIconService
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();
    private static readonly Guid ShellItemImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static ImageSource? TryLoad(string? source, int size)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        size = Math.Clamp(size, 16, 256);
        var key = $"{size}:{source}";
        lock (CacheLock) if (Cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var image = TryLoadShellItem(source, size);
            if (image is not null) lock (CacheLock) Cache.TryAdd(key, image);
            return image;
        }
        catch { return null; }
    }

    public static ImageSource? TryLoadFolder(int size)
    {
        try
        {
            size = Math.Clamp(size, 16, 256);
            var info = new ShellFileInfo();
            var result = SHGetFileInfo("folder", FileAttributesDirectory, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShellFileInfoFlags.Icon | ShellFileInfoFlags.UseFileAttributes | ShellFileInfoFlags.SmallIcon);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(info.Icon); }
        }
        catch { return null; }
    }

    private static ImageSource? TryLoadShellItem(string source, int size)
    {
        var interfaceId = ShellItemImageFactoryId;
        var result = SHCreateItemFromParsingName(source, IntPtr.Zero, ref interfaceId, out var factory);
        if (result < 0 || factory is null) return null;
        try
        {
            result = factory.GetImage(new NativeSize(size, size), ShellItemImageFlags.ResizeToFit, out var bitmap);
            if (result < 0 || bitmap == IntPtr.Zero) return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
                image.Freeze();
                return image;
            }
            finally { DeleteObject(bitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? shellItem);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, ShellFileInfoFlags flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [Flags]
    private enum ShellItemImageFlags { ResizeToFit = 0 }

    [Flags]
    private enum ShellFileInfoFlags : uint { Icon = 0x100, SmallIcon = 0x1, UseFileAttributes = 0x10 }

    private const uint FileAttributesDirectory = 0x10;
}
