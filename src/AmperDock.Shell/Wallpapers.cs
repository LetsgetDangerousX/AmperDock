using System.Runtime.InteropServices;

namespace AmperDock.Shell;

/// <summary>Everything about the desktop background needed to put it back exactly: one picture per monitor, or a slideshow.</summary>
public sealed record WallpaperSnapshot(
    bool Slideshow, List<string> SlideshowItems, int SlideshowOptions, uint SlideshowTick,
    List<MonitorWallpaper> Monitors, int Position);

/// <summary>One monitor's picture (Monitor is the shell's device path for it).</summary>
public sealed record MonitorWallpaper(string Monitor, string Path);

/// <summary>
/// Reads, sets and restores the wallpaper through IDesktopWallpaper (the documented shell interface
/// Settings → Personalisation uses), including slideshows: setting one picture stops a slideshow,
/// and restoring a snapshot starts it again on the same folder with the same timing.
/// </summary>
public static class Wallpapers
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        void SetSlideshow(nint items);
        nint GetSlideshow();
        void SetSlideshowOptions(int options, uint tick);
        void GetSlideshowOptions(out int options, out uint tick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass;

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(nint bc, ref Guid bhid, ref Guid riid, out nint ppv);
        void GetPropertyStore(int flags, ref Guid riid, out nint ppv);
        void GetPropertyDescriptionList(nint keyType, ref Guid riid, out nint ppv);
        void GetAttributes(int attribFlags, uint mask, out uint attribs);
        uint GetCount();
        [return: MarshalAs(UnmanagedType.Interface)] IShellItem GetItemAt(uint index);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bc, ref Guid bhid, ref Guid riid, out nint ppv);
        void GetParent(out IShellItem parent);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetDisplayName(uint sigdn);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bc, out nint pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, nint[] pidls, out nint array);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(nint p);

    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int DSS_SLIDESHOW = 2;

    private static T With<T>(Func<IDesktopWallpaper, T> f)
    {
        var w = (IDesktopWallpaper)new DesktopWallpaperClass();
        try { return f(w); }
        finally { Marshal.ReleaseComObject(w); }
    }

    /// <summary>What's on the desktop right now.</summary>
    public static WallpaperSnapshot Snapshot() => With(w =>
    {
        var monitors = new List<MonitorWallpaper>();
        for (uint i = 0; i < w.GetMonitorDevicePathCount(); i++)
        {
            var id = w.GetMonitorDevicePathAt(i);
            string path;
            try { path = w.GetWallpaper(id); } catch (COMException) { path = ""; }
            monitors.Add(new MonitorWallpaper(id, path ?? ""));
        }
        var slideshow = (w.GetStatus() & DSS_SLIDESHOW) != 0;
        var items = new List<string>();
        int options = 0;
        uint tick = 0;
        if (slideshow)
        {
            try
            {
                var p = w.GetSlideshow();
                if (p != 0)
                {
                    var array = (IShellItemArray)Marshal.GetObjectForIUnknown(p);
                    Marshal.Release(p);
                    for (uint i = 0; i < array.GetCount(); i++)
                    {
                        var item = array.GetItemAt(i);
                        try { items.Add(item.GetDisplayName(SIGDN_FILESYSPATH)); } catch (COMException) { }
                        Marshal.ReleaseComObject(item);
                    }
                    Marshal.ReleaseComObject(array);
                }
                w.GetSlideshowOptions(out options, out tick);
            }
            catch (COMException) { }
        }
        return new WallpaperSnapshot(slideshow && items.Count > 0, items, options, tick, monitors, w.GetPosition());
    });

    /// <summary>The slideshow folder, when a slideshow of one folder is running (where "Add to my slideshow" puts a picture).</summary>
    public static string? SlideshowFolder()
    {
        var s = Snapshot();
        return s.Slideshow && s.SlideshowItems.Count == 1 && Directory.Exists(s.SlideshowItems[0]) ? s.SlideshowItems[0] : null;
    }

    /// <summary>Show one picture on every monitor, filling the screen (this stops a slideshow).</summary>
    public static void Set(string imagePath) => With(w =>
    {
        w.SetPosition(4 /* DWPOS_FILL */);
        w.SetWallpaper(null, imagePath);
        return 0;
    });

    /// <summary>Put the desktop back the way a snapshot found it. Pictures that no longer exist are skipped.</summary>
    public static void Restore(WallpaperSnapshot s) => With(w =>
    {
        w.SetPosition(s.Position);
        if (s.Slideshow)
        {
            var pidls = s.SlideshowItems.Select(path => SHParseDisplayName(path, 0, out var pidl, 0, out _) == 0 ? pidl : 0).Where(p => p != 0).ToArray();
            try
            {
                if (pidls.Length > 0 && SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array) == 0)
                {
                    try
                    {
                        w.SetSlideshow(array);
                        w.SetSlideshowOptions(s.SlideshowOptions, s.SlideshowTick);
                        return 0;
                    }
                    finally { Marshal.Release(array); }
                }
            }
            finally { foreach (var p in pidls) CoTaskMemFree(p); }
        }
        foreach (var m in s.Monitors)
            if (m.Path.Length > 0 && File.Exists(m.Path))
                try { w.SetWallpaper(m.Monitor, m.Path); } catch (COMException) { }
        return 0;
    });
}
