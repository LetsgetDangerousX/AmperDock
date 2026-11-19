using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AmperDock.Shell;

/// <summary>
/// System-wide cursors, through the same registry values the Mouse control panel writes
/// (HKCU\Control Panel\Cursors), applied with SystemParametersInfo(SPI_SETCURSORS).
/// Only the arrow and the link hand are themed; every other cursor stays as it was.
/// </summary>
public static partial class CursorScheme
{
    private const string Key = @"Control Panel\Cursors";
    private static readonly string[] Themed = ["Arrow", "Hand"];
    private static readonly string[] Saved = ["", "Arrow", "Hand", "Scheme Source"];   // "" = the scheme name

    private const uint SPI_SETCURSORS = 0x57, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, nint pv, uint winIni);

    /// <summary>One saved registry value: its type and data exactly as found (Kind null = it didn't exist).</summary>
    public sealed record SavedValue(RegistryValueKind? Kind, string? Text, int? Number);

    /// <summary>The values we change, exactly as they are now (type included). Save this before the first Apply.</summary>
    public static string Snapshot()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        var values = new Dictionary<string, SavedValue>();
        foreach (var n in Saved)
        {
            var raw = k?.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[n] = raw switch
            {
                null => new SavedValue(null, null, null),
                int i => new SavedValue(k!.GetValueKind(n), null, i),
                _ => new SavedValue(k!.GetValueKind(n), raw.ToString(), null),
            };
        }
        return JsonSerializer.Serialize(values);
    }

    /// <summary>Point the arrow and hand at our .cur files and tell Windows to reload cursors.</summary>
    public static void Apply(string arrowPath, string handPath)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
        {
            k.SetValue("Arrow", arrowPath, RegistryValueKind.ExpandString);
            k.SetValue("Hand", handPath, RegistryValueKind.ExpandString);
            k.SetValue("", "AmperDock");   // shows as a custom scheme in the Mouse control panel
        }
        Reload();
    }

    /// <summary>Put back exactly what <see cref="Snapshot"/> recorded: same names, types and data.</summary>
    public static void Restore(string snapshotJson)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, SavedValue>>(snapshotJson) ?? [];
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
        {
            foreach (var name in Saved)
            {
                if (!values.TryGetValue(name, out var v)) continue;
                if (v.Kind is null) { if (k.GetValue(name) is not null) k.DeleteValue(name, false); continue; }
                k.SetValue(name, v.Number is int i ? i : v.Text ?? "", v.Kind.Value);
            }
        }
        Reload();
    }

    /// <summary>True when the registry currently points at files inside <paramref name="folder"/>.</summary>
    public static bool IsOurs(string folder)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return Themed.Any(n => k?.GetValue(n) is string s && s.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    private static void Reload() => SystemParametersInfo(SPI_SETCURSORS, 0, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
}

/// <summary>
/// Custom folder icons through SHGetSetFolderCustomSettings, the documented API behind
/// Properties → Customize → Change Icon (it writes desktop.ini and sets the right attributes).
/// </summary>
public static class FolderIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFOLDERCUSTOMSETTINGS
    {
        public uint dwSize, dwMask;
        public nint pvid;
        public string? pszWebViewTemplate; public uint cchWebViewTemplate;
        public string? pszWebViewTemplateVersion;
        public string? pszInfoTip; public uint cchInfoTip;
        public nint pclsid;
        public uint dwFlags;
        public nint pszIconFile; public uint cchIconFile;
        public int iIconIndex;
        public string? pszLogo; public uint cchLogo;
    }

    private const uint FCSM_ICONFILE = 0x10, FCS_READ = 1, FCS_FORCEWRITE = 2;
    private const int SHCNE_UPDATEITEM = 0x2000, SHCNE_UPDATEDIR = 0x1000, SHCNF_PATHW = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetSetFolderCustomSettings(ref SHFOLDERCUSTOMSETTINGS fcs, string path, uint read);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, int flags, string item1, nint item2);

    /// <summary>The folder's current custom icon (file, index), or (null, 0) if it uses the normal folder icon.</summary>
    public static (string? file, int index) Read(string folder)
    {
        var buf = Marshal.AllocHGlobal(1040 * 2);
        try
        {
            var s = new SHFOLDERCUSTOMSETTINGS
            {
                dwSize = (uint)Marshal.SizeOf<SHFOLDERCUSTOMSETTINGS>(), dwMask = FCSM_ICONFILE, pszIconFile = buf, cchIconFile = 1040,
            };
            if (SHGetSetFolderCustomSettings(ref s, folder, FCS_READ) < 0) return (null, 0);
            var file = Marshal.PtrToStringUni(buf);
            return (string.IsNullOrEmpty(file) ? null : file, s.iIconIndex);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Set (or with <paramref name="iconFile"/> null, clear) a folder's custom icon. Returns an error message or null.</summary>
    public static string? Write(string folder, string? iconFile, int index = 0)
    {
        var str = iconFile is null ? 0 : Marshal.StringToHGlobalUni(iconFile);
        try
        {
            var s = new SHFOLDERCUSTOMSETTINGS
            {
                dwSize = (uint)Marshal.SizeOf<SHFOLDERCUSTOMSETTINGS>(), dwMask = FCSM_ICONFILE,
                pszIconFile = str, cchIconFile = 0, iIconIndex = index,
            };
            var hr = SHGetSetFolderCustomSettings(ref s, folder, FCS_FORCEWRITE);
            if (hr < 0) return hr == unchecked((int)0x80070005)
                ? "Windows didn't allow changing that folder's icon (it's protected)."
                : $"Couldn't change that folder's icon (error 0x{hr:X8}).";
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, folder, 0);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, Path.GetDirectoryName(folder.TrimEnd('\\')) ?? folder, 0);
            return null;
        }
        finally { if (str != 0) Marshal.FreeHGlobal(str); }
    }
}

/// <summary>Bits of system state that decide whether decorative effects should run.</summary>
public static partial class SystemState
{
    [LibraryImport("shell32.dll")] private static partial int SHQueryUserNotificationState(out int state);
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint h, out RECT r);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint h, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint mon, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint h, StringBuilder s, int n);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint h, out uint pid);

    /// <summary>
    /// A fullscreen game, video or presentation in front. Our own full-monitor overlay would fool
    /// SHQueryUserNotificationState's "busy" answer, so that's only trusted for exclusive D3D fullscreen
    /// and presentation mode; otherwise we check whether the foreground window (not the desktop, not
    /// AmperDock) covers its whole monitor.
    /// </summary>
    public static bool FullscreenOrBusy()
    {
        if (SHQueryUserNotificationState(out var s) >= 0 && s is 3 /* RUNNING_D3D_FULL_SCREEN */ or 4 /* PRESENTATION_MODE */) return true;
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return false;
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, 64);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
        var m = mi.rcMonitor;
        return r.L <= m.L && r.T <= m.T && r.R >= m.R && r.B >= m.B;   // covers the monitor, taskbar included
    }

    /// <summary>Windows' "Animation effects" setting (Settings → Accessibility → Visual effects).</summary>
    public static bool AnimationsEnabled() => !SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out var on, 0) || on != 0;
}
