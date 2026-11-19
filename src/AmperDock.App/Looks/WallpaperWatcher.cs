using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmperDock.Core;

namespace AmperDock.App.Looks;

/// <summary>
/// Knows each monitor's wallpaper and the dock colours that go with it, and says when they change
/// (a slideshow moving on, or a new wallpaper picked). Reads the wallpaper through IDesktopWallpaper,
/// the documented shell interface, falling back to the copy Windows keeps of the current wallpaper.
/// </summary>
public sealed class WallpaperWatcher
{
    private static WallpaperWatcher? s_shared;
    public static WallpaperWatcher Shared => s_shared ??= new WallpaperWatcher();

    /// <summary>Raised on the UI thread when any monitor's wallpaper changed.</summary>
    public event Action? Changed;

    private sealed record Monitor(System.Windows.Rect Bounds, string? Path, DateTime Written, WallpaperPalette? Palette);

    private List<Monitor> _monitors = [];
    private string _signature = "";
    private readonly DispatcherTimer _poll;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
    }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass;

    private static string Transcoded => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper");

    private WallpaperWatcher()
    {
        // a slideshow doesn't always announce itself, so look every few seconds (a few file dates; cheap)
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) => Check(), Dispatcher.CurrentDispatcher);
        _poll.Start();
        Check();
    }

    /// <summary>The palette for the monitor showing <paramref name="area"/> (screen pixels), or null if unknown.</summary>
    public WallpaperPalette? PaletteFor(RectF area)
    {
        var cx = area.X + area.W / 2;
        var cy = area.Y + area.H / 2;
        var m = _monitors.FirstOrDefault(m => m.Bounds.Contains(cx, cy)) ?? _monitors.FirstOrDefault();
        return m?.Palette;
    }

    /// <summary>Look again now (after Windows says a setting changed).</summary>
    public void Check()
    {
        List<(System.Windows.Rect bounds, string? path)> found;
        try { found = Read(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { found = []; }
        if (found.Count == 0) found = [(System.Windows.Rect.Empty, null)];

        var resolved = found.Select(f =>
        {
            var path = f.path is { Length: > 0 } p && File.Exists(p) ? p : File.Exists(Transcoded) ? Transcoded : null;
            var written = path is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(path);
            return (f.bounds, path, written);
        }).ToList();
        var signature = string.Join("|", resolved.Select(r => $"{r.bounds}:{r.path}:{r.written.Ticks}"));
        if (signature == _signature) return;
        _signature = signature;

        var known = _monitors;
        _monitors = resolved.Select(r =>
        {
            var same = known.FirstOrDefault(k => k.Path == r.path && k.Written == r.written);
            return new Monitor(r.bounds, r.path, r.written, same?.Palette ?? Analyse(r.path));
        }).ToList();
        Changed?.Invoke();
    }

    private static List<(System.Windows.Rect, string?)> Read()
    {
        var list = new List<(System.Windows.Rect, string?)>();
        var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
        try
        {
            var count = wallpaper.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                var id = wallpaper.GetMonitorDevicePathAt(i);
                var r = wallpaper.GetMonitorRECT(id);
                string? path = null;
                try { path = wallpaper.GetWallpaper(id); } catch (COMException) { }
                list.Add((new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), path));
            }
        }
        finally { Marshal.ReleaseComObject(wallpaper); }
        return list;
    }

    /// <summary>Decode the picture small (about 96 pixels wide) and work out its palette.</summary>
    private static WallpaperPalette? Analyse(string? path)
    {
        if (path is null) return null;
        try
        {
            var bmp = new BitmapImage();
            using (var stream = File.OpenRead(path))
            {
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.DecodePixelWidth = 96;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
            }
            var rgb = new FormatConvertedBitmap(bmp, PixelFormats.Rgb24, null, 0);
            int w = rgb.PixelWidth, h = rgb.PixelHeight;
            var px = new byte[w * h * 3];
            rgb.CopyPixels(px, w * 3, 0);
            return WallpaperPalette.From(px);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException or ArgumentException)
        {
            App.Log(ex);
            return null;
        }
    }
}
