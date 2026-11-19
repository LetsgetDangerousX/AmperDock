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
/// S01, the dock window. A raw Win32 popup with no redirection bitmap, drawn by DockRenderer through
/// DirectComposition, on the WPF UI thread (WPF's message loop pumps it).
///
/// Layout happens in "dock space" (x along the dock, y across it with the screen edge at y = Thickness);
/// <see cref="DockFrame"/> maps that onto whichever edge the dock is on, so bottom, top, left, right and
/// floating docks share one code path.
///
/// Input model: the window never activates (MA_NOACTIVATE), so the app you were in stays in front and
/// "click the running app again to minimise it" works. The window region shrinks to the band at rest so
/// the space next to it stays click-through. Drag empty dock space (or Alt+drag anywhere) to move the dock.
/// </summary>
public sealed class DockHost : IDisposable
{
    private const string ClassName = "AmperDock.Dock";
    private const float DragThreshold = 6;     // DIPs before a press becomes a drag
    private const float RemoveDistance = 60;   // DIPs away from the dock that turn a drag into a remove
    private const float SnapDistance = 96;     // DIPs from a screen edge that snap a moved dock to it

    private static WndProc? s_proc;            // keep the delegate alive for the process lifetime
    private static readonly Dictionary<nint, DockHost> s_hosts = new();

    private readonly SettingsStore _store;
    private readonly ThemeManager _theme;
    private readonly string _containerId;
    private readonly Action<DockHost> _openAppearance;
    private readonly Action _openLooks;
    private readonly nint _hwnd;
    private readonly DockRenderer _renderer;
    private readonly DispatcherTimer _taskTimer;
    private readonly FrameClock _clock;

    private DockAppearance _appearance;
    private DockPlacement _placement;
    private DockFrame _frame;
    private RectF _work;                       // work area of the dock's monitor, screen px
    private string? _monitorKey;
    private float _anchor;                     // dock centre along its length, dock space
    private int _winX, _winY;

    private DockMetrics _baseMetrics, _m;
    private DockStyle _style;
    private float _scale = 1;

    private List<DockItem> _items = [];
    private readonly Dictionary<string, float> _size = new();          // animated size per item id
    private Dictionary<string, List<TaskWindow>> _running = new();
    private nint _foreground;

    private float? _pointerX, _pointerY;       // dock space
    private bool _tracking, _animating, _expanded;
    private long _lastTick;
    private RectF _lastBackground;             // dock space

    // press / drag state
    private int _pressIndex = -1;
    private float _pressX, _pressY;
    private bool _pressed, _dragging, _movingDock, _pressOnEmpty;
    private string? _toast;
    private long _toastUntil;

    public DockHost(SettingsStore store, ThemeManager theme, string containerId, Action<DockHost> openAppearance, Action openLooks)
    {
        _store = store;
        _theme = theme;
        _containerId = containerId;
        _openAppearance = openAppearance;
        _openLooks = openLooks;
        _appearance = store.GetAppearance(containerId);
        _placement = store.GetPlacement(containerId);
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
                hCursor = LoadCursor(0, IDC_ARROW),
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

        _clock = new FrameClock(Dispatcher.CurrentDispatcher, OnFrame);
        Place();
        var (w, h) = _frame.WindowSize;
        _renderer = new DockRenderer(_hwnd, w, h);
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
        SetExpanded(_expanded, force: true);   // the region follows the band, which may now be a different shape
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

    /// <summary>Monitors: device name, monitor handle, work area (screen px).</summary>
    private static List<(string key, nint handle, RECT work)> Monitors()
    {
        var list = new List<(string, nint, RECT)>();
        EnumDisplayMonitors(0, 0, (mon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoEx(mon, ref mi)) list.Add((mi.szDevice, mon, mi.rcWork));
            return true;
        }, 0);
        return list;
    }

    /// <summary>Size and position the window for the current placement (monitor, edge, alignment).</summary>
    private void Place()
    {
        var monitors = Monitors();
        var primary = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var mon = monitors.FirstOrDefault(m => m.key == _placement.MonitorKey);
        if (mon.handle == 0) mon = monitors.FirstOrDefault(m => m.handle == primary);   // monitor unplugged: fall back to primary
        _monitorKey = mon.key;
        _work = new RectF(mon.work.Left, mon.work.Top, mon.work.Right - mon.work.Left, mon.work.Bottom - mon.work.Top);

        _scale = GetDpiForMonitor(mon.handle, 0, out var dpi, out _) == 0 ? dpi / 96f : GetDpiForWindow(_hwnd) / 96f;
        _m = _baseMetrics.Scale(_scale);

        var edge = _placement.Edge;
        var vertical = edge is DockEdge.Left or DockEdge.Right;
        // vertical docks need room beside the icons for labels; horizontal ones above them
        var thickness = MathF.Ceiling(_m.WindowHeight(labelRoom: (vertical ? 220 : 44) * _scale));
        var length = vertical ? _work.H : _work.W;
        _frame = new DockFrame(edge, length, thickness);

        float x, y;
        switch (edge)
        {
            case DockEdge.Top: (x, y) = (_work.X, _work.Y); _anchor = _placement.Alignment * length; break;
            case DockEdge.Left: (x, y) = (_work.X, _work.Y); _anchor = _placement.Alignment * length; break;
            case DockEdge.Right: (x, y) = (_work.Right - thickness, _work.Y); _anchor = _placement.Alignment * length; break;
            case DockEdge.Float:
                // horizontal, centred on the float point: band middle sits on FloatY
                var bandMid = thickness - _m.BandHeight / 2;
                y = Math.Clamp(_placement.FloatY - bandMid, _work.Y - (thickness - _m.BandHeight), _work.Bottom - thickness);
                x = _work.X;
                _anchor = _placement.FloatX - _work.X;
                break;
            default: (x, y) = (_work.X, _work.Bottom - thickness); _anchor = _placement.Alignment * length; break;
        }
        (_winX, _winY) = ((int)x, (int)y);
        var (w, h) = _frame.WindowSize;
        SetWindowPos(_hwnd, 0, _winX, _winY, w, h, SWP_NOACTIVATE | SWP_NOZORDER);
        _renderer?.Resize(w, h);
        if (_renderer is not null) SetExpanded(_expanded, force: true);
    }

    /// <summary>The dock's centre along its length, kept far enough in that the whole dock stays on screen.</summary>
    private float Center(int count)
    {
        var half = (count * _m.IconSize + Math.Max(0, count - 1) * _m.Spacing) / 2 + _m.PadX + _m.MagnifiedSize / 2;
        return half * 2 >= _frame.Length ? _frame.Length / 2 : Math.Clamp(_anchor, half, _frame.Length - half);
    }

    private void ApplyPlacement(DockPlacement p, bool save)
    {
        _placement = p;
        Place();
        Kick();
        if (save) _store.SetPlacement(_containerId, p with { MonitorKey = _monitorKey });
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

    /// <summary>Fetch every item's icon again (after a folder's icon was changed, for example).</summary>
    public void RefreshIcons()
    {
        var iconPx = (int)Math.Min(256, MathF.Ceiling(_m.MagnifiedSize / 32) * 32);
        foreach (var item in _items)
        {
            var src = item.IconPath ?? item.Target;
            var px = src is null ? null : ShellIcons.Get(src, Math.Max(iconPx, 128));
            if (px is not null) _renderer.SetIcon(item.Id, px.Width, px.Height, px.Bgra);
        }
        Kick();
    }

    // ------------------------------------------------------------------ frame loop

    /// <summary>Start the frame loop if it isn't running. It stops itself when nothing is moving.</summary>
    private void Kick()
    {
        if (_animating) return;
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
        var rest = DockLayout.RestCenters(order.Count, _m, Center(order.Count));
        var magnifyX = _dragging || _movingDock ? null : _pointerX;
        var targets = DockLayout.TargetSizes(rest, _m, magnifyX);
        var current = order.Select(i => _size.GetValueOrDefault(i.Id, _m.IconSize)).ToArray();
        var moving = DockLayout.Approach(current, targets, dt);
        for (var i = 0; i < order.Count; i++) _size[order[i].Id] = current[i];

        Render();

        var toastLive = _toast is not null && Environment.TickCount64 < _toastUntil;
        if (!moving && !_dragging && !_movingDock && _pointerX is null && !toastLive)
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
        var rest = DockLayout.RestCenters(others.Count, _m, Center(others.Count + 1));
        others.Insert(DockLayout.InsertIndex(rest, _pointerX ?? 0), dragged);
        return others;
    }

    private bool IsRemoveGesture()
    {
        var bandTop = _frame.Thickness - _m.BandHeight;
        return _dragging && _pointerY is float y && y < bandTop - RemoveDistance * _scale;
    }

    private LabelSide LabelSideForEdge => _placement.Edge switch
    {
        DockEdge.Top => LabelSide.Below,
        DockEdge.Left => LabelSide.Right,
        DockEdge.Right => LabelSide.Left,
        _ => LabelSide.Above,
    };

    /// <summary>A label anchored just past the far side of a dock-space rectangle (away from the screen edge).</summary>
    private LabelDraw LabelAt(string text, float alongX, float acrossY)
    {
        var (x, y) = _frame.ToWindow(alongX, acrossY);
        return new LabelDraw(text, x, y, LabelSideForEdge);
    }

    private void Render()
    {
        if (_renderer is null) return;
        var order = DisplayOrder();
        var sizes = order.Select(i => _size.GetValueOrDefault(i.Id, _m.IconSize)).ToArray();
        var center = Center(Math.Max(order.Count, _dragging ? order.Count : 0));
        var (rects, bg) = DockLayout.Arrange(sizes, _m, center, _frame.Thickness);

        // empty dock: a short band with a hint, so a first-run user knows where to drop things
        if (order.Count == 0 && !_dragging)
        {
            var w = _m.IconSize * 4;
            bg = new RectF(center - w / 2, _frame.Thickness - _m.BandHeight, w, _m.BandHeight);
        }

        var icons = new List<IconDraw>(order.Count + 1);
        var indicators = new List<IndicatorDraw>();
        var draggedId = _dragging && _pressIndex >= 0 && _pressIndex < _items.Count ? _items[_pressIndex].Id : null;
        for (var i = 0; i < order.Count; i++)
        {
            var item = order[i];
            if (item.Id == draggedId) continue;   // drawn last, under the cursor
            icons.Add(new IconDraw(item.Id, _frame.ToWindow(rects[i]), 1, false));
            if (_running.TryGetValue(item.Id, out var wins))
            {
                var (ix, iy) = _frame.ToWindow(rects[i].CenterX, rects[i].Bottom + _m.IndicatorGap + _m.IndicatorSize / 2);
                indicators.Add(new IndicatorDraw(ix, iy, _m.IndicatorSize, wins.Any(w => w.Hwnd == _foreground)));
            }
        }
        if (draggedId is not null && _pointerX is float px && _pointerY is float py)
        {
            var s = _m.IconSize * 1.1f;
            icons.Add(new IconDraw(draggedId, _frame.ToWindow(new RectF(px - s / 2, py - s / 2, s, s)), IsRemoveGesture() ? 0.4f : 0.85f, false));
        }

        LabelDraw? label = null;
        var gap = 8 * _scale;
        if (_toast is not null && Environment.TickCount64 < _toastUntil)
            label = LabelAt(_toast, center, bg.Y - gap);
        else if (order.Count == 0 && !_dragging)
            label = LabelAt("Drop apps here and they'll stay put.", center, bg.Y - gap);
        else if (!_dragging && !_movingDock && _pointerX is float hx && _pointerY is float hy)
        {
            var hit = DockLayout.HitTest(rects, hx, hy, _m.Spacing / 2);
            if (hit >= 0)
            {
                var item = order[hit];
                var text = _running.TryGetValue(item.Id, out var w) && w.Count > 1 ? $"{item.Label} · {w.Count} windows" : item.Label;
                label = LabelAt(text, rects[hit].CenterX, rects[hit].Y - 10 * _scale);
            }
        }

        _renderer.Draw(new DockScene(_frame.ToWindow(bg), _appearance.RadiusFor(bg.H), icons, indicators, label, _scale), _style);
        _lastBackground = bg;
    }

    /// <summary>
    /// Window region: the whole window while the pointer is on the dock (room for magnified icons and
    /// labels), just the band at rest, so the space beside it stays click-through.
    /// </summary>
    private void SetExpanded(bool expanded, bool force = false)
    {
        if (!force && expanded == _expanded && expanded) return;
        _expanded = expanded;
        var (w, h) = _frame.WindowSize;
        var t = _frame.Thickness;
        var bg = _lastBackground.W > 0 ? _lastBackground : new RectF(0, t - _m.BandHeight, _frame.Length, _m.BandHeight);
        RectF r;
        if (expanded)
            r = new RectF(0, 0, _frame.Length, t);
        else if (_items.Count == 0)   // keep the first-run hint visible
            r = new RectF(bg.CenterX - 200 * _scale, bg.Y - 44 * _scale, 400 * _scale, t - bg.Y + 44 * _scale);
        else
            r = new RectF(bg.X - 2, bg.Y - 2, bg.W + 4, t - bg.Y + 2);
        var win = _frame.ToWindow(r);
        var rgn = expanded ? CreateRectRgn(0, 0, w, h)
            : CreateRectRgn((int)MathF.Floor(win.X), (int)MathF.Floor(win.Y), (int)MathF.Ceiling(win.Right), (int)MathF.Ceiling(win.Bottom));
        SetWindowRgn(_hwnd, rgn, true);   // the system owns rgn now
    }

    private void Toast(string text)
    {
        _toast = text;
        _toastUntil = Environment.TickCount64 + 3500;
        Kick();
    }

    // ------------------------------------------------------------------ input

    private (float x, float y) DockPoint(nint l) => _frame.ToDock(LoWord(l), HiWord(l));

    private (RectF[] rects, RectF bg) CurrentRects()
    {
        var sizes = _items.Select(i => _size.GetValueOrDefault(i.Id, _m.IconSize)).ToArray();
        return DockLayout.Arrange(sizes, _m, Center(_items.Count), _frame.Thickness);
    }

    private bool OverEmptyDock(float x, float y)
    {
        var (rects, bg) = CurrentRects();
        var band = _items.Count == 0 ? _lastBackground : bg;
        return band.Contains(x, y) && DockLayout.HitTest(rects, x, y, 0) < 0;
    }

    private static bool AltDown => GetKeyState(VK_MENU) < 0;

    private nint WndProcImpl(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;

            case WM_SETCURSOR:
            {
                // a "move" cursor over empty dock space hints that you can drag the dock itself
                GetCursorPos(out var sp);
                ScreenToClient(_hwnd, ref sp);
                var (dx, dy) = _frame.ToDock(sp.X, sp.Y);
                if (_movingDock || AltDown || OverEmptyDock(dx, dy)) { SetCursor(LoadCursor(0, IDC_SIZEALL)); return 1; }
                break;
            }

            case WM_MOUSEMOVE:
            {
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = _hwnd };
                    _tracking = TrackMouseEvent(ref tme);
                }
                if (_movingDock) { MoveDockToCursor(save: false); return 0; }
                var (x, y) = DockPoint(l);
                _pointerX = x;
                _pointerY = y;
                var past = MathF.Abs(x - _pressX) > DragThreshold * _scale || MathF.Abs(y - _pressY) > DragThreshold * _scale;
                if (_pressed && past && !_dragging)
                {
                    if (_pressOnEmpty) StartMovingDock();
                    else if (_pressIndex >= 0) _dragging = true;
                }
                Kick();
                return 0;
            }

            case WM_MOUSELEAVE:
                _tracking = false;
                if (GetCapture() != _hwnd) { _pointerX = _pointerY = null; Kick(); }
                return 0;

            case WM_LBUTTONDOWN:
            {
                var (x, y) = DockPoint(l);
                var (rects, _) = CurrentRects();
                _pressIndex = DockLayout.HitTest(rects, x, y, _m.Spacing / 2);
                _pressOnEmpty = AltDown || _pressIndex < 0;   // Alt+drag moves the dock from anywhere
                _pressX = x;
                _pressY = y;
                _pressed = true;
                SetCapture(_hwnd);
                return 0;
            }

            case WM_LBUTTONUP:
                OnRelease();
                return 0;

            case WM_MBUTTONUP:
            {
                var (x, y) = DockPoint(l);
                var (rects, _) = CurrentRects();
                var i = DockLayout.HitTest(rects, x, y, _m.Spacing / 2);
                if (i >= 0) LaunchOrActivate(_items[i], forceNew: true);   // middle-click = new instance
                return 0;
            }

            case WM_CAPTURECHANGED:
                if (_dragging || _movingDock || _pressed)
                {
                    if (_movingDock) MoveDockToCursor(save: true);
                    _dragging = _movingDock = _pressed = false;
                    _pressIndex = -1;
                    Kick();
                }
                return 0;

            case WM_RBUTTONUP:
            {
                var (x, y) = DockPoint(l);
                ShowMenu(x, y);
                return 0;
            }

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

    // ------------------------------------------------------------------ moving the whole dock

    private void StartMovingDock()
    {
        _movingDock = true;
        _pointerX = _pointerY = null;
        Toast("Drop near any screen edge to dock it there, or anywhere else to float.");
    }

    /// <summary>Follow the cursor: snap to the nearest edge of whichever monitor it's on, or float under it.</summary>
    private void MoveDockToCursor(bool save)
    {
        GetCursorPos(out var p);
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var info = Monitors().FirstOrDefault(m => m.handle == mon);
        if (info.handle == 0) return;
        var work = new RectF(info.work.Left, info.work.Top, info.work.Right - info.work.Left, info.work.Bottom - info.work.Top);
        var dpi = GetDpiForMonitor(mon, 0, out var d, out _) == 0 ? d / 96f : _scale;
        var next = DockPlacement.Snap(p.X, p.Y, work, SnapDistance * dpi, info.key);
        if (!save && next == _placement) return;
        ApplyPlacement(next, save);
    }

    private void OnRelease()
    {
        var index = _pressIndex;
        var wasDragging = _dragging;
        var wasMoving = _movingDock;
        var wasPressed = _pressed;
        _pressIndex = -1;
        _dragging = false;
        _pressed = false;
        if (wasMoving)
        {
            MoveDockToCursor(save: true);
            _movingDock = false;
            Toast(_placement.Edge == DockEdge.Float ? "Dock floating here." : $"Dock moved to the {_placement.Edge.ToString().ToLowerInvariant()} edge.");
        }
        if (GetCapture() == _hwnd) ReleaseCapture();
        if (wasMoving || !wasPressed || index < 0 || index >= _items.Count) { Kick(); return; }
        var item = _items[index];

        if (!wasDragging)
        {
            LaunchOrActivate(item, forceNew: false);
            return;
        }

        if (IsRemoveGestureAt(_pointerY))
        {
            _store.RemoveItem(item.Id);   // undo comes with the toast component later
            Reload();
            Toast($"Removed {item.Label}");
            return;
        }

        var others = _items.Where((_, i) => i != index).ToList();
        var rest = DockLayout.RestCenters(others.Count, _m, Center(_items.Count));
        var newIndex = DockLayout.InsertIndex(rest, _pointerX ?? _pressX);
        if (newIndex != index) _store.MoveItem(item.Id, newIndex);
        Reload();
    }

    private bool IsRemoveGestureAt(float? y) => y is float v && v < _frame.Thickness - _m.BandHeight - RemoveDistance * _scale;

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
            var (x, _) = _frame.ToDock(pt.X, pt.Y);
            var rest = DockLayout.RestCenters(_items.Count, _m, Center(_items.Count));
            AddPaths(paths, DockLayout.InsertIndex(rest, x));
        }
        finally { Native.DragFinish(hDrop); }
    }

    // ------------------------------------------------------------------ menu

    private void ShowMenu(float x, float y)
    {
        var (rects, _) = CurrentRects();
        var hit = DockLayout.HitTest(rects, x, y, _m.Spacing / 2);
        var menu = CreatePopupMenu();
        var position = CreatePopupMenu();
        const int Remove = 1, OpenLocation = 2, AddApp = 3, AddFolder = 4, Looks = 8, Appearance = 9, Quit = 11;
        const int PosBottom = 20, PosTop = 21, PosLeft = 22, PosRight = 23, PosFloat = 24, PosTip = 25;
        if (hit >= 0)
        {
            AppendMenu(menu, MF_STRING, OpenLocation, "Open file location");
            AppendMenu(menu, MF_STRING, Remove, "Remove from dock");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
        }
        AppendMenu(menu, MF_STRING, AddApp, "Add an app or file…");
        AppendMenu(menu, MF_STRING, AddFolder, "Add a folder…");
        AppendMenu(menu, MF_SEPARATOR, 0, null);

        uint Check(DockEdge e) => MF_STRING | (_placement.Edge == e ? MF_CHECKED : 0);
        AppendMenu(position, Check(DockEdge.Bottom), PosBottom, "Bottom");
        AppendMenu(position, Check(DockEdge.Top), PosTop, "Top");
        AppendMenu(position, Check(DockEdge.Left), PosLeft, "Left");
        AppendMenu(position, Check(DockEdge.Right), PosRight, "Right");
        AppendMenu(position, Check(DockEdge.Float), PosFloat, "Floating");
        AppendMenu(position, MF_SEPARATOR, 0, null);
        AppendMenu(position, MF_STRING | MF_GRAYED, PosTip, "Tip: drag an empty part of the dock (or Alt+drag) to move it anywhere");
        AppendMenu(menu, MF_POPUP, position, "Position");
        AppendMenu(menu, MF_STRING, Appearance, "Dock appearance…");
        AppendMenu(menu, MF_STRING, Looks, "Look packs: cursors, trails, folder icons…");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, Quit, "Quit AmperDock");

        var (wx, wy) = _frame.ToWindow(x, y);
        var pt = new POINT { X = (int)wx, Y = (int)wy };
        ClientToScreen(_hwnd, ref pt);
        SetForegroundWindow(_hwnd);   // required so the menu closes when you click elsewhere
        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(menu);            // also destroys the submenu

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
            case AddApp:
                AddFromPicker(folder: false, after: hit);
                break;
            case AddFolder:
                AddFromPicker(folder: true, after: hit);
                break;
            case PosBottom or PosTop or PosLeft or PosRight:
                var edge = cmd switch { PosTop => DockEdge.Top, PosLeft => DockEdge.Left, PosRight => DockEdge.Right, _ => DockEdge.Bottom };
                ApplyPlacement(new DockPlacement(edge, _monitorKey, 0.5f), save: true);
                break;
            case PosFloat:
                // float a little above where a bottom dock would sit, centred on the monitor
                ApplyPlacement(new DockPlacement(DockEdge.Float, _monitorKey, 0.5f,
                    (int)(_work.X + _work.W / 2), (int)(_work.Bottom - _work.H * 0.25f)), save: true);
                break;
            case Appearance:
                _openAppearance(this);
                break;
            case Looks:
                _openLooks();
                break;
            case Quit:
                System.Windows.Application.Current.Shutdown();
                break;
        }
    }

    /// <summary>
    /// "Add an app or file…" / "Add a folder…": the standard Windows pickers. New items go after the
    /// item you right-clicked, or at the end of the dock.
    /// </summary>
    private void AddFromPicker(bool folder, int after)
    {
        string[] paths;
        if (folder)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add a folder to the dock", Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            paths = dlg.FolderNames;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add an app or file to the dock",
                Multiselect = true,
                DereferenceLinks = false,   // keep .lnk shortcuts as shortcuts
                Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.appref-ms|All files|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            };
            if (dlg.ShowDialog() != true) return;
            paths = dlg.FileNames;
        }
        AddPaths(paths, after >= 0 ? after + 1 : _items.Count);
    }

    /// <summary>Shared by drag-and-drop and the pickers: skip duplicates, insert, refresh.</summary>
    private void AddPaths(IReadOnlyList<string> paths, int index)
    {
        var already = _items.Where(i => i.Target is not null).Select(i => i.Target!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = paths.Where(p => !already.Contains(p)).Select(Shortcuts.FromDroppedPath).ToList();
        if (fresh.Count == 0) { Toast(paths.Count == 1 ? "That's already on the dock." : "Those are already on the dock."); return; }
        _store.InsertItems(_containerId, index, fresh);
        Reload();
        Toast(fresh.Count == 1 ? $"Added {fresh[0].Label}" : $"Added {fresh.Count} items");
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
