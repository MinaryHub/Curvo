using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Curvo.Interop;

namespace Curvo.UI;

/// <summary>실행 파일 경로에서 작은 아이콘을 읽어 WPF 이미지 소스로 변환한다.</summary>
internal static class IconLoader
{
    private const uint FileAttributeNormal = 0x80;

    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Load(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        if (Cache.TryGetValue(executablePath, out BitmapSource? cached)) return cached;

        BitmapSource? source = Extract(executablePath);
        Cache[executablePath] = source;
        return source;
    }

    private static BitmapSource? Extract(string path)
    {
        var info = new Win32.SHFILEINFOW();
        uint flags = Win32.SHGFI_ICON | Win32.SHGFI_SMALLICON | Win32.SHGFI_USEFILEATTRIBUTES;
        IntPtr result = Win32.SHGetFileInfoW(path, FileAttributeNormal, ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.SHFILEINFOW>(), flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.DestroyIcon(info.hIcon);
        }
    }
}
