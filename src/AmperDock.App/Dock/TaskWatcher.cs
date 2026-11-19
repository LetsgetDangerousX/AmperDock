using System.Runtime.InteropServices;
using System.Windows.Threading;
using AmperDock.Shell;

namespace AmperDock.App.Dock;

/// <summary>
/// Keeps the list of open windows current, for every dock at once. Windows tells us when a window
/// appears, disappears, is renamed, cloaked, minimised or brought to the front (SetWinEventHook,
/// out of context, so nothing is injected into other apps). Bursts of events are folded into one
/// refresh a few milliseconds later. A slow safety-net timer catches anything the events missed.
/// </summary>
public sealed class TaskWatcher : IDisposable
{
    private static TaskWatcher? s_shared;

    /// <summary>The one watcher, created on first use (on the UI thread).</summary>
    public static TaskWatcher Shared => s_shared ??= new TaskWatcher();

    public IReadOnlyList<TaskWindow> Windows { get; private set; } = [];
    public nint Foreground { get; private set; }

    /// <summary>Raised on the UI thread after the window list or the foreground window changed.</summary>
    public event Action? Changed;

    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);

    private const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C, EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;

    private readonly WinEventProc _proc;   // kept alive: Windows calls it for as long as the hooks exist
    private readonly List<nint> _hooks = new();
    private readonly DispatcherTimer _debounce, _safetyNet;
    private HashSet<nint> _known = new();

    private TaskWatcher()
    {
        _proc = OnEvent;
        // separate small ranges: one big range would include location changes, which fire on every mouse move
        foreach (var (min, max) in new[]
                 {
                     (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND), (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                     (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE), (EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE),
                     (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
                 })
        {
            var h = SetWinEventHook(min, max, 0, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h != 0) _hooks.Add(h);
        }
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        _safetyNet = new DispatcherTimer(TimeSpan.FromSeconds(_hooks.Count > 0 ? 5 : 1), DispatcherPriority.Background,
            (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        _safetyNet.Start();
        Refresh();
    }

    private void OnEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == 0) return;   // only whole windows, not carets, scrollbars...
        if (ev == EVENT_OBJECT_DESTROY)
        {
            if (!_known.Contains(hwnd)) return;   // the window is gone, so only care if it was one of ours
        }
        else if (ev != EVENT_SYSTEM_FOREGROUND && Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd)
        {
            return;   // child controls appearing and renaming themselves
        }
        if (ev == EVENT_SYSTEM_FOREGROUND && _debounce.IsEnabled is false)
        {
            // the front window changed: worth showing at once, the list itself rarely changes with it
            Foreground = Native.GetForegroundWindow();
            Changed?.Invoke();
        }
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    /// <summary>Re-read the window list now (also called after the dock activates or launches something).</summary>
    public void Refresh()
    {
        _debounce.Stop();
        var windows = Tasks.Snapshot();
        var fg = Native.GetForegroundWindow();
        var set = windows.Select(w => w.Hwnd).ToHashSet();
        var changed = fg != Foreground || !set.SetEquals(_known)
                      || windows.Count != Windows.Count
                      || windows.Zip(Windows).Any(p => p.First != p.Second);
        Windows = windows;
        Foreground = fg;
        _known = set;
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        _debounce.Stop();
        _safetyNet.Stop();
        if (ReferenceEquals(s_shared, this)) s_shared = null;
    }
}
