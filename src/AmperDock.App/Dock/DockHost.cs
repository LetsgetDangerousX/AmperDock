using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using AmperDock.App.Theme;
using AmperDock.Core;
using AmperDock.Render;
using AmperDock.Shell;
using static AmperDock.App.Dock.Win32;

namespace AmperDock.App.Dock;

/// <summary>
/// S01, the dock window (vertical slice: bottom edge, primary monitor, horizontal).
/// A raw Win32 popup with no redirection bitmap, drawn by DockRenderer through DirectComposition.
/// Lives on the WPF UI thread; WPF's message loop pumps it.
///
/// Input model: the window never activates (MA_NOACTIVATE), so the app the user was in stays in the
/// foreground and "click the running app again to minimise it" works. The window region shrinks to
/// the dock band at rest so the strip above it stays click-through.
/// </summary>
public sealed class DockHost : IDisposable
{
    private const string ClassName = "AmperDock.Dock";
    private const float DragThreshold = 6;     // DIPs before a press becomes a drag
    private const float RemoveDistance = 60;   // DIPs above the dock that turn a drag into a remove

    private static WndProc? s_proc;            // keep the delegate alive for the process lifetime
    private static readonly Dictionary<nint, DockHost> s_hosts = new();

    private readonly SettingsStore _store;
    private readonly ThemeManager _theme;
    private readonly string _containerId;
    private readonly Action _openGallery;
    private readonly Action<DockHost> _openAppearance;
    private DockAppearance _appearance;
    private readonly nint _hwnd;
    private readonly DockRenderer _renderer;
    private readonly DispatcherTimer _taskTimer;
    private readonly FrameClock _clock;

    private DockMetrics _baseMetrics, _m;
    private DockStyle _style;
    private float _scale = 1;
    private int _width, _height;

    private List<DockItem> _items = [];
    private readonly Dictionary<string, float> _size = new();          // animated size per item id
    private Dictionary<string, List<TaskWindow>> _running = new();
    private nint _foreground;

    private float? _pointerX, _pointerY;
    private bool _tracking, _animating, _expanded;
    private long _lastTick;

    // press / drag state
    private int _pressIndex = -1;
    private float _pressX, _pressY;
    private bool _dragging;
    private string? _toast;                    // short message shown in the label slot (errors, hints)
    private long _toastUntil;

    public DockHost(SettingsStore store, ThemeManager theme, string containerId, Action openGallery, Action<DockHost> openAppearance)
    {
        _store = store;
        _theme = theme;
        _containerId = containerId;
        _openGallery = openGallery;
        _openAppearance = openAppearance;
        _appearance = store.GetAppearance(containerId);
        _baseMetrics = theme.DockMetrics();
        _m = _baseMetrics;
        _style = ComposeStyle();
        theme.Changed += () => { _style = ComposeStyle(); Render(); };

        var inst = GetModuleHandle(0);
        if (s_proc is null)
        {
            s_proc = StaticWndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = inst,
                hCursor = LoadCursor(0, 32512),   // IDC_ARROW
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _hwnd = CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, ClassName, "AmperDock", WS_POPUP,
            0, 0, 100, 100, 0, 0, inst, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        s_hosts[_hwnd] = this;

        // Explorer runs at medium integrity like us, but allow the drop messages through UIPI anyway
        Native.ChangeWindowMessageFilterEx(_hwnd, (uint)Native.WM_DROPFILES, Native.MSGFLT_ALLOW, 0);
        Native.ChangeWindowMessageFilterEx(_hwnd, Native.WM_COPYDATA, Native.MSGFLT_ALLOW, 0);
        Native.ChangeWindowMessageFilterEx(_hwnd, Native.WM_COPYGLOBALDATA, Native.MSGFLT_ALLOW, 0);
        Native.DragAcceptFiles(_hwnd, true);

        Place();
        _clock = new FrameClock(Dispatcher.CurrentDispatcher, OnFrame);
        _renderer = new DockRenderer(_hwnd, _width, _height);
        Reload();
        ShowWindow(_hwnd, SW_SHOWNA);

        _taskTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshTasks(), Dispatcher.CurrentDispatcher);
        _taskTimer.Start();
        RefreshTasks();
    }

    // ------------------------------------------------------------------ appearance

    public DockAppearance Appearance => _appearance;

    /// <summary>Show an appearance on the live dock without saving it (the Appearance window's live preview).</summary>
    public void PreviewAppearance(DockAppearance appearance)
    {
        _appearance = appearance.Normalised();
        _style = ComposeStyle();
        SetExpanded(_expanded);   // the region follows the band, which may now be a different shape
        Render();
    }

    public void SaveAppearance(DockAppearance appearance)
    {
        PreviewAppearance(appearance);
        _store.SetAppearance(_containerId, _appearance);
    }

    /// <summary>Theme colours plus the user's style, colours, opacity and outline choices.</summary>
    private DockStyle ComposeStyle()
    {
        var t = _theme.DockStyle();
        var a = _appearance;
        var k = a.Opacity / 100f;
        var baseFill = a.Color is null ? t.Background : Argb.FromRgba(a.Color);
        var outlineColour = a.OutlineColor is null ? t.Outline : Argb.FromRgba(a.OutlineColor);
        var none = new Argb(0);

        var fill = baseFill.WithAlpha(k);
        Argb? end = null;
        var highlight = none;
        var outline = a.Outline && a.Opacity > 0 ? outlineColour.WithAlpha(MathF.Min(1, k * 1.5f)) : none;

        switch (a.Style)
        {
            case DockStyles.Glass:
                highlight = t.Highlight.WithAlphaScaled(k);
                break;
            case DockStyles.Gradient:
                end = (a.Color2 is null ? Lighter(baseFill) : Argb.FromRgba(a.Color2)).WithAlpha(k);
                break;
            case DockStyles.Outline:
                fill = none;   // just the line; opacity sets how strong it is
                outline = a.Opacity > 0 ? outlineColour.WithAlpha(k) : none;
                break;
        }
        return t with
        {
            Background = fill,
            BackgroundEnd = end,
            GradientAngle = a.GradientAngle,
            Outline = outline,
            OutlineWidth = a.Style == DockStyles.Outline ? MathF.Max(1.5f, a.OutlineWidth) : a.OutlineWidth,
            Highlight = highlight,
            Shadow = a.Style == DockStyles.Outline ? none : t.Shadow.WithAlphaScaled(k),
        };
    }

    /// <summary>The default gradient end: the fill mixed 45% toward white.</summary>
    private static Argb Lighter(Argb c)
    {
        uint Mix(int shift) => (uint)(((c.Value >> shift) & 0xFF) + (255 - ((c.Value >> shift) & 0xFF)) * 0.45f);
        return new Argb(0xFF000000 | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0));
    }

    // ------------------------------------------------------------------ placement

    /// <summary>Bottom edge of the primary monitor's work area, full width, tall enough for magnified icons + label.</summary>
    private void Place()
    {
        var mon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        _scale = GetDpiForWindow(_hwnd) / 96f;
        _m = _baseMetrics.Scale(_scale);
        var work = mi.rcWork;
        _width = work.Right - work.Left;
        _height = (int)MathF.Ceiling(_m.WindowHeight(labelRoom: 44 * _scale));
        SetWindowPos(_hwnd, 0, work.Left, work.Bottom - _height, _width, _height, SWP_NOACTIVATE | SWP_NOZORDER);
        _renderer?.Resize(_width, _height);
    }

    // ------------------------------------------------------------------ data

    private void Reload()
    {
        _items = _store.GetItems(_containerId).ToList();
        var iconPx = (int)Math.Min(256, MathF.Ceiling(_m.MagnifiedSize / 32) * 32);
        foreach (var item in _items)
        {
            _size.TryAdd(item.Id, _m.IconSize);
            if (_renderer.HasIcon(item.Id)) continue;
            var src = item.IconPath ?? item.Target;
            var px = src is null ? null : ShellIcons.Get(src, Math.Max(iconPx, 128));
            if (px is not null) _renderer.SetIcon(item.Id, px.Width, px.Height, px.Bgra);
        }
        foreach (var gone in _size.Keys.Except(_items.Select(i => i.Id)).ToList()) _size.Remove(gone);
        RefreshTasks();
        Kick();
    }

    private void RefreshTasks()
    {
        var windows = Tasks.Snapshot(_hwnd);
        var byExe = windows.Where(w => w.ExePath is not null)
            .GroupBy(w => w.ExePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var running = new Dictionary<string, List<TaskWindow>>();
        foreach (var item in _items)
        {
            var exe = Shortcuts.ExeFor(item);
            if (exe is not null && byExe.TryGetValue(exe, out var list)) running[item.Id] = list;
        }
        var fg = Native.GetForegroundWindow();
        var changed = fg != _foreground || running.Count != _running.Count
                      || running.Any(kv => !_running.TryGetValue(kv.Key, out var old) || old.Count != kv.Value.Count);
        _running = running;
        _foreground = fg;
        if (changed) Render();
    }

    // ------------------------------------------------------------------ frame loop

    /// <summary>Start the frame loop if it isn't running. It stops itself when nothing is moving.</summary>
    private void Kick()
    {
        if (_animating) { return; }
        _animating = true;
        _lastTick = Stopwatch.GetTimestamp();
        _clock.Start();
        SetExpanded(true);
    }

    private void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (float)Math.Min(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;

        var order = DisplayOrder();
        var rest = DockLayout.RestCenters(order.Count, _m, _width / 2f);
        var magnifyX = _dragging ? null : _pointerX;
        var targets = DockLayout.TargetSizes(rest, _m, magnifyX);
        var current = order.Select(i => _size.GetValueOrDefault(i.Id, _m.IconSize)).ToArray();
        var moving = DockLayout.Approach(current, targets, dt);
        for (var i = 0; i < order.Count; i++) _size[order[i].Id] = current[i];

        Render();

        var toastLive = _toast is not null && Environment.TickCount64 < _toastUntil;
        if (!moving && !_dragging && _pointerX is null && !toastLive)
        {
            _clock.Stop();
            _animating = false;
            _toast = null;
            SetExpanded(false);
            Render();
        }
    }

    /// <summary>Items in display order. While dragging, the dragged item sits at the slot under the cursor.</summary>
    private List<DockItem> DisplayOrder()
    {
        if (!_dragging || _pressIndex < 0 || _pressIndex >= _items.Count) return _items;
        var dragged = _items[_pressIndex];
        var others = _items.Where((_, i) => i != _pressIndex).ToList();
        if (IsRemoveGesture()) return others;
        var rest = DockLayout.RestCenters(others.Count, _m, _width / 2f);
        others.Insert(DockLayout.InsertIndex(rest, _pointerX ?? 0), dragged);
        return others;
    }

    private bool IsRemoveGesture()
    {
        var bandTop = _height - _m.BandHeight;
        return _dragging && _pointerY is float y && y < bandTop - RemoveDistance * _scale;
    }

    private void Render()
    {
        if (_renderer is null) return;
        var order = DisplayOrder();
        var sizes = order.Select(i => _size.GetValueOrDefault(i.Id, _m.IconSize)).ToArray();
        var (rects, bg) = DockLayout.Arrange(sizes, _m, _width / 2f, _height);

        // empty dock: a short band with a hint, so a first-run user knows where to drop things
        if (order.Count == 0 && !_dragging)
        {
            var w = _m.IconSize * 4;
            bg = new RectF(_width / 2f - w / 2, _height - _m.BandHeight, w, _m.BandHeight);
        }

        var icons = new List<IconDraw>(order.Count + 1);
        var indicators = new List<IndicatorDraw>();
        var draggedId = _dragging && _pressIndex >= 0 && _pressIndex < _items.Count ? _items[_pressIndex].Id : null;
        for (var i = 0; i < order.Count; i++)
        {
            var item = order[i];
            if (item.Id == draggedId) continue;   // drawn last, under the cursor
            icons.Add(new IconDraw(item.Id, rects[i], 1, false));
            if (_running.TryGetValue(item.Id, out var wins))
                indicators.Add(new IndicatorDraw(rects[i].CenterX, rects[i].Bottom + _m.IndicatorGap + _m.IndicatorSize / 2,
                    _m.IndicatorSize, wins.Any(w => w.Hwnd == _foreground)));
        }
        if (draggedId is not null && _pointerX is float px && _pointerY is float py)
        {
            var s = _m.IconSize * 1.1f;
            icons.Add(new IconDraw(draggedId, new RectF(px - s / 2, py - s / 2, s, s), IsRemoveGesture() ? 0.4f : 0.85f, false));
        }

        LabelDraw? label = null;
        if (_toast is not null && Environment.TickCount64 < _toastUntil)
            label = new LabelDraw(_toast, _width / 2f, bg.Y - 8 * _scale);
        else if (order.Count == 0 && !_dragging)
            label = new LabelDraw("Drop apps here and they'll stay put.", _width / 2f, bg.Y - 8 * _scale);
        else if (!_dragging && _pointerX is float hx && _pointerY is float hy)
        {
            var hit = DockLayout.HitTest(rects, hx, hy, _m.Spacing / 2);
            if (hit >= 0)
            {
                var item = order[hit];
                var text = _running.TryGetValue(item.Id, out var w) && w.Count > 1 ? $"{item.Label} · {w.Count} windows" : item.Label;
                label = new LabelDraw(text, rects[hit].CenterX, rects[hit].Y - 10 * _scale);
            }
        }

        _renderer.Draw(new DockScene(bg, _appearance.RadiusFor(bg.H), icons, indicators, label, _scale), _style);
        _lastBackground = bg;
    }

    private RectF _lastBackground;

    /// <summary>
    /// Window region: the full window height over the dock's width while the pointer is on it (room
    /// for magnified icons and labels), just the band at rest, so the strip above stays click-through.
    /// </summary>
    private void SetExpanded(bool expanded)
    {
        if (expanded == _expanded && expanded) return;
        _expanded = expanded;
        var bg = _lastBackground.W > 0 ? _lastBackground : new RectF(0, _height - _m.BandHeight, _width, _m.BandHeight);
        nint rgn;
        if (expanded)
            rgn = CreateRectRgn(0, 0, _width, _height);
        else if (_items.Count == 0)   // keep the first-run hint above the empty band visible
            rgn = CreateRectRgn((int)(_width / 2f - 200 * _scale), (int)(bg.Y - 44 * _scale), (int)(_width / 2f + 200 * _scale), _height);
        else
            rgn = CreateRectRgn((int)bg.X - 2, (int)bg.Y - 2, (int)MathF.Ceiling(bg.Right) + 2, _height);
        SetWindowRgn(_hwnd, rgn, true);   // the system owns rgn now
    }

    private void Toast(string text)
    {
        _toast = text;
        _toastUntil = Environment.TickCount64 + 3500;
        Kick();
    }

    // ------------------------------------------------------------------ input

    private nint WndProcImpl(uint msg, nint w, nint l)
    {
        if (msg is WM_MOUSEMOVE or WM_MOUSELEAVE or WM_LBUTTONDOWN or WM_LBUTTONUP) App.Trace($"msg {msg:X} x={LoWord(l)} y={HiWord(l)}");
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;

            case WM_MOUSEMOVE:
            {
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = _hwnd };
                    _tracking = TrackMouseEvent(ref tme);
                }
                _pointerX = LoWord(l);
                _pointerY = HiWord(l);
                if (_pressIndex >= 0 && !_dragging &&
                    (MathF.Abs(_pointerX.Value - _pressX) > DragThreshold * _scale || MathF.Abs(_pointerY.Value - _pressY) > DragThreshold * _scale))
                    _dragging = true;
                Kick();
                return 0;
            }

            case WM_MOUSELEAVE:
                _tracking = false;
                if (GetCapture() != _hwnd) { _pointerX = _pointerY = null; Kick(); }
                return 0;

            case WM_LBUTTONDOWN:
            {
                var (rects, _) = CurrentRects();
                _pressIndex = DockLayout.HitTest(rects, LoWord(l), HiWord(l), _m.Spacing / 2);
                _pressX = LoWord(l);
                _pressY = HiWord(l);
                if (_pressIndex >= 0) SetCapture(_hwnd);
                return 0;
            }

            case WM_LBUTTONUP:
                OnRelease(middle: false);
                return 0;

            case WM_MBUTTONUP:
            {
                var (rects, _) = CurrentRects();
                var i = DockLayout.HitTest(rects, LoWord(l), HiWord(l), _m.Spacing / 2);
                if (i >= 0) LaunchOrActivate(_items[i], forceNew: true);   // middle-click = new instance
                return 0;
            }

            case WM_CAPTURECHANGED:
                if (_dragging) { _dragging = false; _pressIndex = -1; Kick(); }
                return 0;

            case WM_RBUTTONUP:
                ShowMenu(LoWord(l), HiWord(l));
                return 0;

            case WM_DROPFILES:
                OnDropFiles(w);
                return 0;

            case WM_DPICHANGED:
            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE:   // the work area moves when the taskbar changes size/position
                Place();
                Kick();
                return 0;
        }
        return DefWindowProcW(_hwnd, msg, w, l);
    }

    private (RectF[] rects, RectF bg) CurrentRects()
    {
        var sizes = _items.Select(i => _size.GetValueOrDefault(i.Id, _m.IconSize)).ToArray();
        return DockLayout.Arrange(sizes, _m, _width / 2f, _height);
    }

    private void OnRelease(bool middle)
    {
        var index = _pressIndex;
        var wasDragging = _dragging;
        _pressIndex = -1;
        _dragging = false;
        if (GetCapture() == _hwnd) ReleaseCapture();
        if (index < 0 || index >= _items.Count) { Kick(); return; }
        var item = _items[index];

        if (!wasDragging)
        {
            LaunchOrActivate(item, forceNew: false);
            return;
        }

        if (_pointerY is float y && y < _height - _m.BandHeight - RemoveDistance * _scale)
        {
            _store.RemoveItem(item.Id);   // undo comes with the toast component in milestone 2
            Reload();
            Toast($"Removed {item.Label}");
            return;
        }

        var others = _items.Where((_, i) => i != index).ToList();
        var rest = DockLayout.RestCenters(others.Count, _m, _width / 2f);
        var newIndex = DockLayout.InsertIndex(rest, _pointerX ?? _pressX);
        if (newIndex != index) _store.MoveItem(item.Id, newIndex);
        Reload();
    }

    private void LaunchOrActivate(DockItem item, bool forceNew)
    {
        if (!forceNew && _running.TryGetValue(item.Id, out var windows) && windows.Count > 0)
        {
            Tasks.Activate(windows);
            RefreshTasks();
            return;
        }
        var error = Tasks.Launch(item, forceNew);
        if (error is not null) Toast(error);
        // the running indicator appears on the next task refresh (within a second)
    }

    private void OnDropFiles(nint hDrop)
    {
        try
        {
            var count = Native.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                var len = Native.DragQueryFile(hDrop, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                Native.DragQueryFile(hDrop, i, sb, len + 1);
                paths.Add(sb.ToString());
            }
            Native.DragQueryPoint(hDrop, out var pt);
            var rest = DockLayout.RestCenters(_items.Count, _m, _width / 2f);
            var index = DockLayout.InsertIndex(rest, pt.X);

            var already = _items.Where(i => i.Target is not null).Select(i => i.Target!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = paths.Where(p => !already.Contains(p)).Select(Shortcuts.FromDroppedPath).ToList();
            if (fresh.Count == 0) { Toast("That's already on the dock."); return; }
            _store.InsertItems(_containerId, index, fresh);
            Reload();
        }
        finally { Native.DragFinish(hDrop); }
    }

    private void ShowMenu(int x, int y)
    {
        var (rects, _) = CurrentRects();
        var hit = DockLayout.HitTest(rects, x, y, _m.Spacing / 2);
        var menu = CreatePopupMenu();
        const int Remove = 1, OpenLocation = 2, Appearance = 9, Gallery = 10, Quit = 11;
        if (hit >= 0)
        {
            AppendMenu(menu, MF_STRING, OpenLocation, "Open file location");
            AppendMenu(menu, MF_STRING, Remove, "Remove from dock");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
        }
        AppendMenu(menu, MF_STRING, Appearance, "Dock appearance…");
        AppendMenu(menu, MF_STRING, Gallery, "Component gallery…");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, Quit, "Quit AmperDock");

        var pt = new POINT { X = x, Y = y };
        ClientToScreen(_hwnd, ref pt);
        SetForegroundWindow(_hwnd);   // required so the menu closes when you click elsewhere
        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(menu);

        switch (cmd)
        {
            case Remove when hit >= 0:
                var item = _items[hit];
                _store.RemoveItem(item.Id);
                Reload();
                Toast($"Removed {item.Label}");
                break;
            case OpenLocation when hit >= 0:
                var t = _items[hit].Target;
                if (t is not null && (File.Exists(t) || Directory.Exists(t)))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t}\"") { UseShellExecute = true })?.Dispose();
                break;
            case Appearance:
                _openAppearance(this);
                break;
            case Gallery:
                _openGallery();
                break;
            case Quit:
                System.Windows.Application.Current.Shutdown();
                break;
        }
    }

    private static nint StaticWndProc(nint hwnd, uint msg, nint w, nint l)
    {
        if (s_hosts.TryGetValue(hwnd, out var host))
        {
            try { return host.WndProcImpl(msg, w, l); }
            catch (Exception ex)
            {
                // never let an exception unwind through a native frame; log and carry on
                Debug.WriteLine($"[AmperDock] wndproc {msg:X}: {ex}");
                App.Log(ex);
                return 0;
            }
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    public void Dispose()
    {
        _taskTimer.Stop();
        _clock.Dispose();
        s_hosts.Remove(_hwnd);
        _renderer.Dispose();
        DestroyWindow(_hwnd);
    }
}
