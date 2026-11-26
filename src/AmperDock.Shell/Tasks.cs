using System.Diagnostics;
using System.Text;
using AmperDock.Core;

namespace AmperDock.Shell;

/// <summary>
/// A top-level window that would appear on the taskbar. For Store apps, <see cref="Pid"/> and
/// <see cref="ExePath"/> are the app's own process (not ApplicationFrameHost) when it can be found.
/// <see cref="AppId"/> is the window's AUMID when it has one (Store apps, web apps, some desktop apps).
/// </summary>
public sealed record TaskWindow(nint Hwnd, uint Pid, string? ExePath, string? AppId = null);

/// <summary>Running-window tracking: which windows would show on the taskbar right now.</summary>
public static class Tasks
{
    private sealed record Info(uint Pid, string? Exe, string? AppId, long FirstSeen);

    // per-window facts that rarely change, so a refresh after every window event stays cheap
    private static readonly Dictionary<nint, (uint pid, Info info)> s_info = new();
    private static readonly Dictionary<uint, (string? exe, string? appId)> s_proc = new();
    private static readonly uint s_ownPid = (uint)Environment.ProcessId;

    /// <summary>Windows that would show on the taskbar: visible, unowned (or app-window), not tool windows, not cloaked.
    /// Our own windows (docks, settings) are left out.</summary>
    public static List<TaskWindow> Snapshot()
    {
        var list = new List<TaskWindow>();
        var seen = new HashSet<nint>();
        var now = Environment.TickCount64;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            var owner = Native.GetWindow(hwnd, Native.GW_OWNER);
            var appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (!appWindow && (owner != 0 || (ex & Native.WS_EX_TOOLWINDOW) != 0)) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            if (Native.GetWindowTextLength(hwnd) == 0) return true;

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == s_ownPid) return true;
            seen.Add(hwnd);
            var have = s_info.TryGetValue(hwnd, out var cached) && cached.pid == pid;
            var info = cached.info;
            // an app can stamp its id on a window just after showing it: keep asking for a few seconds
            if (!have || (info.AppId is null && now - info.FirstSeen < 5000))
            {
                info = Describe(hwnd, pid, have ? info.FirstSeen : now);
                s_info[hwnd] = (pid, info);
            }
            list.Add(new TaskWindow(hwnd, info.Pid, info.Exe, info.AppId));
            return true;
        }, 0);
        foreach (var gone in s_info.Keys.Where(h => !seen.Contains(h)).ToList()) s_info.Remove(gone);
        // forget processes with no windows left, so a reused process id can't inherit an old program's facts
        var live = list.Select(w => w.Pid).ToHashSet();
        foreach (var pid in s_proc.Keys.Where(p => !live.Contains(p)).ToList()) s_proc.Remove(pid);
        return list;
    }

    private static Info Describe(nint hwnd, uint pid, long firstSeen)
    {
        var appPid = pid;
        // a Store app's window is a frame owned by ApplicationFrameHost; the app itself is a child CoreWindow
        if (Native.ClassOf(hwnd) == "ApplicationFrameWindow")
        {
            Native.EnumChildWindows(hwnd, (child, _) =>
            {
                if (Native.ClassOf(child) != "Windows.UI.Core.CoreWindow") return true;
                Native.GetWindowThreadProcessId(child, out var cp);
                if (cp != pid) { appPid = cp; return false; }
                return true;
            }, 0);
        }
        var (exe, processAppId) = ProcessFacts(appPid);
        return new Info(appPid, exe, AppIds.ForWindow(hwnd) ?? processAppId, firstSeen);
    }

    /// <summary>A process's program file and, for packaged apps, its app id. Cached per process id.</summary>
    private static (string? exe, string? appId) ProcessFacts(uint pid)
    {
        if (s_proc.TryGetValue(pid, out var known)) return known;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return (null, null);   // e.g. elevated processes: running-but-unmatched; not cached, may become readable
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            var exe = Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            return s_proc[pid] = (exe, AppIds.ForProcess(h));
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Bring one window to the front, restoring it first if it's minimised.</summary>
    public static void Focus(nint hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hwnd);
    }

    /// <summary>Click on a running item: restore if minimised, minimise if it's already in front, else bring forward.</summary>
    public static void Activate(IReadOnlyList<TaskWindow> windows)
    {
        if (windows.Count == 0) return;
        var fg = Native.GetForegroundWindow();
        var front = windows.FirstOrDefault(w => w.Hwnd == fg);
        if (front is not null && windows.Count == 1)
        {
            Native.ShowWindow(front.Hwnd, Native.SW_MINIMIZE);
            return;
        }
        // cycle: pick the window after the current foreground one, or the first
        var idx = front is null ? 0 : (windows.ToList().IndexOf(front) + 1) % windows.Count;
        var target = windows[idx].Hwnd;
        if (Native.IsIconic(target)) Native.ShowWindow(target, Native.SW_RESTORE);
        Native.SetForegroundWindow(target);
    }

    /// <summary>Launch an item's target through the shell. Returns an error message, or null on success.</summary>
    public static string? Launch(DockItem item, bool forceNewInstance = false)
    {
        if (item.Target is null) return "This item has nothing to open.";
        var exists = File.Exists(item.Target) || Directory.Exists(item.Target)
                     || item.Target.Contains("://", StringComparison.Ordinal) || item.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        if (!exists) return $"Can't find \"{item.Target}\". It may have been moved or uninstalled.";
        try
        {
            var psi = new ProcessStartInfo(item.Target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(item.Arguments)) psi.Arguments = item.Arguments;
            // start in the item's own "Start in" folder, else the app's folder, else the user's folder: never
            // AmperDock's (an app sitting in the install folder stops AmperDock updating)
            if (!string.IsNullOrEmpty(item.StartIn) && Directory.Exists(item.StartIn)) psi.WorkingDirectory = item.StartIn;
            else if (File.Exists(item.Target) && Path.GetDirectoryName(item.Target) is { Length: > 0 } dir) psi.WorkingDirectory = dir;
            else psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (item.RunAsAdmin) psi.Verb = "runas";
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;   // the user said no to the UAC prompt; that's not an error
        }
        catch (Exception ex)
        {
            return $"Couldn't open {item.Label}: {ex.Message}";
        }
    }
}
