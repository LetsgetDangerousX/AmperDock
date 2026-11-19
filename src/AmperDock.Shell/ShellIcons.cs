using System.Runtime.InteropServices;

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
