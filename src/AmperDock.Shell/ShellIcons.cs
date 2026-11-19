using System.Runtime.InteropServices;
using AmperDock.Core;

namespace AmperDock.Shell;

/// <summary>Premultiplied BGRA pixels, top-down rows, stride = Width * 4.</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

/// <summary>Large icons via IShellItemImageFactory (the documented way to get 256px shell icons).</summary>
public static class ShellIcons
{
    [Flags]
    private enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 0x10 }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out nint phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    /// <summary>Icon for a file, folder, .lnk or URL-ish parsing name. Returns null if Windows has none.</summary>
    public static IconPixels? Get(string parsingName, int size)
    {
        IShellItemImageFactory? factory = null;
        nint hbmp = 0;
        try
        {
            SHCreateItemFromParsingName(parsingName, 0, typeof(IShellItemImageFactory).GUID, out factory);
            var hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.BiggerSizeOk | SIIGBF.IconOnly, out hbmp);
            if (hr < 0 || hbmp == 0) return null;
            return FromHBitmap(hbmp);
        }
        catch (COMException) { return null; }
        catch (FileNotFoundException) { return null; }
        catch (ArgumentException) { return null; }
        finally
        {
            if (hbmp != 0) Native.DeleteObject(hbmp);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    /// <summary>
    /// The icon to show for a dock item: its custom icon (an image, an .ico, or "file,index" inside an
    /// .exe/.dll) when it has one that loads, otherwise the target's own shell icon.
    /// </summary>
    public static IconPixels? ForItem(string? iconPath, string? target, int size)
    {
        if (IconRef.Parse(iconPath) is { } r && Custom(r, size) is { } custom) return custom;
        return target is null ? null : Get(target, size);
    }

    /// <summary>Load a custom icon reference. Null if the file is missing or holds no such icon.</summary>
    public static IconPixels? Custom(IconRef r, int size)
    {
        var file = Environment.ExpandEnvironmentVariables(r.File);
        if (!File.Exists(file)) return null;
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ImageExts.Contains(ext)) return Image(file, size);
        if (r.IsIconResource) return Extract(file, r.Index, size);
        return Get(file, size);   // anything else: whatever icon Windows shows for that file
    }

    /// <summary>A picture file scaled to fit a square (keeps its shape, transparent padding).</summary>
    private static IconPixels? Image(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        nint hbmp = 0;
        try
        {
            SHCreateItemFromParsingName(path, 0, typeof(IShellItemImageFactory).GUID, out factory);
            var hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.ResizeToFit | SIIGBF.ThumbnailOnly, out hbmp);
            if (hr < 0 || hbmp == 0) return null;
            var px = FromHBitmap(hbmp);
            return px is null ? null : Square(px);
        }
        catch (COMException) { return null; }
        catch (FileNotFoundException) { return null; }
        catch (ArgumentException) { return null; }
        finally
        {
            if (hbmp != 0) Native.DeleteObject(hbmp);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static IconPixels Square(IconPixels px)
    {
        if (px.Width == px.Height) return px;
        var n = Math.Max(px.Width, px.Height);
        var buf = new byte[n * n * 4];
        int ox = (n - px.Width) / 2, oy = (n - px.Height) / 2;
        for (var y = 0; y < px.Height; y++)
            Buffer.BlockCopy(px.Bgra, y * px.Width * 4, buf, ((y + oy) * n + ox) * 4, px.Width * 4);
        return new IconPixels(n, n, buf);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public int fIcon, xHotspot, yHotspot; public nint hbmMask, hbmColor; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(string file, int index, uint flags, out nint large, out nint small, uint sizes);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);

    /// <summary>
    /// Icon number <paramref name="index"/> (or resource id, when negative) from an .exe, .dll or .ico,
    /// at the requested size. Uses SHDefExtractIcon, the documented API the shell uses itself.
    /// </summary>
    public static IconPixels? Extract(string file, int index, int size)
    {
        size = Math.Clamp(size, 16, 256);
        if (SHDefExtractIconW(file, index, 0, out var large, out var small, (uint)((16 << 16) | size)) != 0 || large == 0)
        {
            if (small != 0) DestroyIcon(small);
            return null;
        }
        if (small != 0) DestroyIcon(small);
        try { return FromHIcon(large); }
        finally { DestroyIcon(large); }
    }

    private static IconPixels? FromHIcon(nint icon)
    {
        if (!GetIconInfo(icon, out var ii)) return null;
        try
        {
            if (ii.hbmColor == 0) return null;   // monochrome icons: not worth supporting on a dock
            var color = Raw(ii.hbmColor);
            if (color is null) return null;
            var bits = color.Bgra;
            var hasAlpha = false;
            for (var i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha && Raw(ii.hbmMask) is { } mask && mask.Width == color.Width && mask.Height >= color.Height)
            {
                // old-style icon: the AND mask says which pixels are transparent (white = see-through)
                for (var i = 0; i < bits.Length; i += 4) bits[i + 3] = mask.Bgra[i] > 127 ? (byte)0 : (byte)255;
            }
            NormaliseAlpha(bits);
            return color;
        }
        finally
        {
            if (ii.hbmColor != 0) Native.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != 0) Native.DeleteObject(ii.hbmMask);
        }
    }

    /// <summary>An HBITMAP's pixels as 32-bit BGRA, untouched (alpha as stored, possibly all zero).</summary>
    private static IconPixels? Raw(nint hbmp)
    {
        if (Native.GetObject(hbmp, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        var hdc = Native.GetDC(0);
        try { if (Native.GetDIBits(hdc, hbmp, 0, (uint)h, bits, ref bi, 0) == 0) return null; }
        finally { Native.ReleaseDC(0, hdc); }
        return new IconPixels(w, h, bits);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int PickIconDlg(nint owner, System.Text.StringBuilder path, uint cch, ref int index);

    /// <summary>
    /// Windows' own "Change Icon" dialog, starting in <paramref name="file"/> (an .exe/.dll/.ico).
    /// Returns the chosen icon, or null if cancelled.
    /// </summary>
    public static IconRef? PickIcon(nint owner, string file, int index)
    {
        var sb = new System.Text.StringBuilder(file, 1024);
        if (PickIconDlg(owner, sb, (uint)sb.Capacity, ref index) == 0) return null;
        return new IconRef(sb.ToString(), index);
    }

    private static IconPixels? FromHBitmap(nint hbmp)
    {
        if (Native.GetObject(hbmp, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h,           // negative = top-down rows
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var bits = new byte[w * h * 4];
        var hdc = Native.GetDC(0);
        try
        {
            if (Native.GetDIBits(hdc, hbmp, 0, (uint)h, bits, ref bi, 0) == 0) return null;
        }
        finally { Native.ReleaseDC(0, hdc); }

        NormaliseAlpha(bits);
        return new IconPixels(w, h, bits);
    }

    /// <summary>
    /// Shell bitmaps come back either premultiplied, straight alpha, or with no alpha at all depending on
    /// the source. Detect which and convert to premultiplied, which is what Direct2D wants.
    /// </summary>
    internal static void NormaliseAlpha(byte[] bgra)
    {
        bool anyAlpha = false, straight = false;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a != 0) anyAlpha = true;
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a) straight = true;
        }
        if (!anyAlpha)
        {
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;   // opaque image without an alpha channel
            return;
        }
        if (!straight) return;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
        }
    }
}
