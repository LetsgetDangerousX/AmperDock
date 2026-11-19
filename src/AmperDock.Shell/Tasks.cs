using System.Diagnostics;
using System.Text;
using AmperDock.Core;

namespace AmperDock.Shell;

/// <summary>A top-level window that would appear on the taskbar.</summary>
public sealed record TaskWindow(nint Hwnd, uint Pid, string? ExePath);

/// <summary>
/// Running-window tracking. The slice polls on a timer; the event-hook version
/// (SetWinEventHook + RegisterShellHookWindow, architecture.md) replaces the polling in milestone 1.
/// </summary>
public static class Tasks
{
    /// <summary>Windows that would show on the taskbar: visible, unowned (or app-window), not tool windows, not cloaked.</summary>
    public static List<TaskWindow> Snapshot(nint ownHwnd = 0)
    {
        var list = new List<TaskWindow>();
        var exeByPid = new Dictionary<uint, string?>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == ownHwnd || !Native.IsWindowVisible(hwnd)) return true;
            var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            var owner = Native.GetWindow(hwnd, Native.GW_OWNER);
            var appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (!appWindow && (owner != 0 || (ex & Native.WS_EX_TOOLWINDOW) != 0)) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            if (Native.GetWindowTextLength(hwnd) == 0) return true;

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!exeByPid.TryGetValue(pid, out var exe)) exeByPid[pid] = exe = ExeOf(pid);
            list.Add(new TaskWindow(hwnd, pid, exe));
            return true;
        }, 0);
        return list;
    }

    private static string? ExeOf(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;   // e.g. elevated processes; they show as running-but-unmatched for now
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
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
            if (!string.IsNullOrEmpty(item.StartIn) && Directory.Exists(item.StartIn)) psi.WorkingDirectory = item.StartIn;
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
