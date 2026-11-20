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
/// <summary>What a dock's menu can ask the app to do (things that involve other windows or other docks).</summary>
public sealed record DockCommands(Action<DockHost> OpenAppearance, Action OpenLooks, Action<DockHost> NewDock, Action<DockHost> RemoveDock,
    Action<string, DockHost> AddWidget);

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
    private readonly DockCommands _commands;
    private readonly nint _hwnd;
    private readonly DockRenderer _renderer;
    private readonly Action _onTasks;
    private readonly Action _onWallpaper;
    private readonly Dictionary<string, (string? target, ItemKeys keys)> _keys = new();
    private readonly FrameClock _clock;

    private DockAppearance _appearance;
    private DockPlacement _placement;
    private DockBehavior _behavior;

    // auto-hide
    private float _hide;                       // 0 = showing, 1 = tucked past the screen edge
    private long _pointerLeftAt;               // when the pointer last left the dock or its previews
    private long _edgeSince;                   // when the pointer reached the edge strip of a tucked-away dock
    private const int HideDelayMs = 700;

    /// <summary>The choices in the Auto-hide menu: how long to rest at the edge before the dock comes back.</summary>
    private static readonly (int Ms, string Menu, string Spoken)[] RevealDelays =
    [
        (0, "Come back as soon as I touch the edge", "a moment"),
        (300, "Come back after a &short rest (0.3 seconds)", "0.3 seconds"),
        (600, "Come back after a &moment (0.6 seconds)", "0.6 seconds"),
        (1000, "Come back after &1 second", "1 second"),
        (2000, "Come back after &2 seconds", "2 seconds"),
    ];

    // window previews
    private PreviewWindow? _preview;
    private readonly DispatcherTimer _previewTimer;
    private string? _hoverItem, _previewKey;   // the running item under the pointer; what the previews show
    private long _hoverSince, _previewLeftAt = -1;
    private const int PreviewDelayMs = 800, PreviewGraceMs = 350;
    private bool _menuOpen;                    // the right-click menu is up: no previews over it
    private string? _previewMuted;             // right-clicked item: no previews until the pointer moves off it
    private long _menuClosedAt;
    private DockFrame _frame;
    private RectF _work;                       // work area of the dock's monitor, screen px
    private string? _monitorKey;
    private float _anchor;                     // dock centre along its length, dock space
    private int _winX, _winY;

    private DockMetrics _baseMetrics, _m;
    private DockStyle _style;
    private float _scale = 1;

    private List<DockItem> _items = [];            // what the dock shows: the pinned items, then open apps that aren't pinned
    private List<DockItem> _pinned = [];           // the items saved in the store
    private List<DockItem> _openApps = [];
    private readonly Dictionary<string, (string label, string? target)> _openAppInfo = new();
    private const string OpenAppType = "open-app";

    private static bool IsOpenApp(DockItem item) => item.Type == OpenAppType;
    private bool ShowsOpenApps => _behavior.ShowOpenApps ?? IsMain;
    private readonly Dictionary<string, float> _size = new();          // animated size per item id
    private Dictionary<string, List<TaskWindow>> _running = new();
    private nint _foreground;

    private float? _pointerX, _pointerY;       // dock space
    private bool _tracking, _animating, _expanded;
    private long _lastTick;
    private RectF _lastBackground;             // dock space
    private RectF _lastExtent;                 // dock space: the band plus decorations

    // press / drag state
    private int _pressIndex = -1;
    private float _pressX, _pressY;
    private bool _pressed, _dragging, _movingDock, _pressOnEmpty;
    private string? _toast;
    private long _toastUntil;

    // the empty dock's "Drop apps here" hint: shown for a few seconds, then it gets out of the way
    private long _hintUntil;
    private bool _wasEmpty;
    private readonly DispatcherTimer _hintTimer;
    private const int HintMs = 6000;

    public DockHost(SettingsStore store, ThemeManager theme, string containerId, DockCommands commands)
    {
        _store = store;
        _theme = theme;
        _containerId = containerId;
        _commands = commands;
        _appearance = store.GetAppearance(containerId);
        _placement = store.GetPlacement(containerId);
        _behavior = store.GetBehavior(containerId);
        _pointerLeftAt = Environment.TickCount64;
        _hintTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(HintMs), DispatcherPriority.Background, (_, _) => HintExpired(), Dispatcher.CurrentDispatcher);
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Normal, (_, _) => PreviewTick(), Dispatcher.CurrentDispatcher);
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
        // the full drag-and-drop way, so apps dragged from the Start menu land too (it doesn't send files)
        _drop = new DropTarget(OnDrop);
        if (!_drop.Register(_hwnd)) _drop = null;   // WM_DROPFILES still works for files from Explorer

        _clock = new FrameClock(Dispatcher.CurrentDispatcher, OnFrame);
        Place();
        var (w, h) = _frame.WindowSize;
        _renderer = new DockRenderer(_hwnd, w, h);
        LoadThemePictures();
        Reload();
        ShowWindow(_hwnd, SW_SHOWNA);

        _onTasks = RefreshTasks;
        TaskWatcher.Shared.Changed += _onTasks;
        _onWallpaper = () => { if (FollowsWallpaper) ApplyWallpaperColours(); };
        Looks.WallpaperWatcher.Shared.Changed += _onWallpaper;
        ApplyWallpaperColours();
        RefreshTasks();
    }

    // ------------------------------------------------------------------ appearance

    public DockAppearance Appearance => _appearance;

    public string ContainerId => _containerId;
    public bool IsMain => _containerId == SettingsStore.MainDockId;
    public DockPlacement Placement => _placement with { MonitorKey = _monitorKey };
    public int ItemCount => _pinned.Count;

    /// <summary>The work area of the dock's monitor, in screen pixels.</summary>
    public RectF WorkArea => _work;

    /// <summary>Show an appearance on the live dock without saving it (the Appearance window's live preview).</summary>
    public void PreviewAppearance(DockAppearance appearance)
    {
        var resized = appearance.Normalised().IndicatorSize != _appearance.IndicatorSize;
        _appearance = appearance.Normalised();
        _style = ComposeStyle();
        LoadThemePictures();
        if (resized) Place();                   // a bigger indicator makes the band taller
        ApplyWallpaperColours();
        SetExpanded(_expanded, force: true);   // the region follows the band, which may now be a different shape
        Render();
    }

    public void SaveAppearance(DockAppearance appearance)
    {
        PreviewAppearance(appearance);
        _store.SetAppearance(_containerId, _appearance);
    }

    private DockThemeInfo? Theme => _appearance.Decorations ? DockThemeCatalog.Shared.Find(_appearance.Theme) : null;

    /// <summary>Upload the current theme's decorations and indicator picture to the GPU (once each).</summary>
    private void LoadThemePictures()
    {
        if (_renderer is null) return;
        var keys = new List<string>();
        if (DockThemeCatalog.Shared.Indicators.TryGetValue(_appearance.Indicator, out var ind)) keys.Add(ind);
        if (Theme is { } theme)
        {
            keys.AddRange(theme.Decor.Select(d => d.Sprite));
            keys.AddRange(theme.Attach.Where(a => a.Sprite is not null).Select(a => a.Sprite!));
        }
        foreach (var key in keys.Distinct())
        {
            if (_renderer.HasIcon("theme:" + key)) continue;
            if (DockThemeCatalog.Pixels(key) is { } px) _renderer.SetIcon("theme:" + key, px.w, px.h, px.px);
        }
    }

    /// <summary>Theme colours plus the user's style, colours, opacity and outline choices.</summary>
    private bool FollowsWallpaper => _appearance.MatchWallpaper || _appearance.IconTint > 0;

    /// <summary>The wallpaper's colours for this dock's monitor, when the dock or its icons follow the wallpaper.</summary>
    private WallpaperPalette? WallpaperColours =>
        FollowsWallpaper ? Looks.WallpaperWatcher.Shared.PaletteFor(_work.W > 0 ? _work : new RectF(0, 0, 1, 1)) : null;

    /// <summary>Re-colour the dock and/or tint the icons (each is its own choice) after the wallpaper or a setting changed.</summary>
    private void ApplyWallpaperColours()
    {
        if (_renderer is null) return;
        _style = ComposeStyle();
        var p = WallpaperColours;
        var tint = p is not null && _appearance.IconTint > 0;
        _renderer.SetIconTint(tint ? Argb.FromRgba(p!.Tint) : default, tint ? _appearance.IconTint / 100f : 0);
        Render();
    }

    private DockStyle ComposeStyle()
    {
        var t = _theme.DockStyle();
        var a = _appearance;
        if (_appearance.MatchWallpaper && WallpaperColours is { } wp)   // the wallpaper picks the dock's colours; the rest stays the person's choice
            a = a with { Color = wp.Fill, Color2 = wp.Fill2, OutlineColor = wp.Outline, IndicatorColor = wp.Indicator };
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
        var indicatorKind = a.Indicator switch
        {
            DockIndicators.Paw => IndicatorKind.Paw,
            DockIndicators.Dot => IndicatorKind.Dot,
            _ => IndicatorKind.Sprite,
        };
        var indicatorSprite = DockThemeCatalog.Shared.Indicators.TryGetValue(a.Indicator, out var ind) ? "theme:" + ind : null;
        if (indicatorKind == IndicatorKind.Sprite && indicatorSprite is null) indicatorKind = IndicatorKind.Paw;   // picture missing
        var indicator = a.IndicatorColor is null ? t.Indicator : Argb.FromRgba(a.IndicatorColor).WithAlpha(0.8f);
        var indicatorFocused = a.IndicatorColor is null ? t.IndicatorFocused : Argb.FromRgba(a.IndicatorColor);
        return t with
        {
            IndicatorKind = indicatorKind,
            IndicatorSprite = indicatorSprite,
            Indicator = indicator,
            IndicatorFocused = indicatorFocused,
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
        // our own reserved strip is part of the work area Windows reports less: give it back to ourselves
        if (_reserved > 0)
            _work = _placement.Edge switch
            {
                DockEdge.Top => new RectF(_work.X, _work.Y - _reserved, _work.W, _work.H + _reserved),
                DockEdge.Left => new RectF(_work.X - _reserved, _work.Y, _work.W + _reserved, _work.H),
                DockEdge.Right => new RectF(_work.X, _work.Y, _work.W + _reserved, _work.H),
                _ => new RectF(_work.X, _work.Y, _work.W, _work.H + _reserved),
            };

        _scale = GetDpiForMonitor(mon.handle, 0, out var dpi, out _) == 0 ? dpi / 96f : GetDpiForWindow(_hwnd) / 96f;
        _m = (_baseMetrics with { IndicatorSize = _baseMetrics.IndicatorSize * DockIndicators.SizeFactor(_appearance.IndicatorSize) }).Scale(_scale);

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
        UpdateAppBar();
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
        _pinned = _store.GetItems(_containerId).ToList();
        _items = [.. _pinned, .. _openApps];
        var empty = _pinned.Count == 0;
        if (empty && !_wasEmpty) ShowHint();   // at start-up, or the last item was just removed
        _wasEmpty = empty;
        var iconPx = IconPixelSize;
        foreach (var item in _items)
        {
            _size.TryAdd(item.Id, _m.IconSize);
            if (_renderer.HasIcon(item.Id)) continue;
            LoadIcon(item, iconPx);
        }
        foreach (var gone in _size.Keys.Except(_items.Select(i => i.Id)).ToList()) _size.Remove(gone);
        RefreshTasks();
        Kick();
    }

    /// <summary>Which items have open windows, and whether one of them is in front. Runs after every window change.</summary>
    private void RefreshTasks()
    {
        var watcher = TaskWatcher.Shared;
        var keys = new List<TaskMatch.ItemKey>(_pinned.Count);
        foreach (var item in _pinned)
        {
            // working out a shortcut's program means reading the .lnk: do it once per target, not per window event
            if (!_keys.TryGetValue(item.Id, out var k) || k.target != item.Target)
                _keys[item.Id] = k = (item.Target, AppIds.KeysFor(item.Target));
            keys.Add(new TaskMatch.ItemKey(item.Id, k.keys.AppId, k.keys.Exe));
        }
        var (running, leftovers) = TaskMatch.Assign(keys, watcher.Windows, w => w.AppId, w => w.ExePath);
        UpdateOpenApps(leftovers, running);
        var fg = watcher.Foreground;
        var changed = fg != _foreground || running.Count != _running.Count
                      || running.Any(kv => !_running.TryGetValue(kv.Key, out var old) || !old.SequenceEqual(kv.Value));
        var clickedAway = _foreground != 0 && fg != _foreground;   // not the very first look at the front window
        _running = running;
        _foreground = fg;
        if (changed)
        {
            if (clickedAway && _hintUntil > 0 && _pointerX is null) HintExpired();
            RefreshPreview();
            Render();
        }
    }

    /// <summary>
    /// Apps with windows open that nothing on the dock opens get a place after the pinned items, in the
    /// order they were opened, and leave when their last window closes. Grouped like the taskbar: by app
    /// id when there is one, else by program file.
    /// </summary>
    private void UpdateOpenApps(List<TaskWindow> leftovers, Dictionary<string, List<TaskWindow>> running)
    {
        var apps = new List<DockItem>();
        if (ShowsOpenApps)
        {
            foreach (var group in leftovers.Where(w => w.AppId is not null || w.ExePath is not null)
                         .GroupBy(w => (w.AppId ?? w.ExePath!).ToLowerInvariant()))
            {
                var first = group.First();
                var id = "open:" + group.Key;
                if (!_openAppInfo.TryGetValue(id, out var info))
                {
                    // a Store or web app opens by its app id; a desktop program by its file
                    var name = first.AppId is { } aumid ? AppIds.DisplayName(aumid) : null;
                    var target = name is not null ? AppIds.AppsFolderPrefix + first.AppId : first.ExePath;
                    var label = name ?? (first.ExePath is { } exe ? Shortcuts.LabelFor(exe) : "App");
                    _openAppInfo[id] = info = (label, target);
                }
                apps.Add(new DockItem(id, _containerId, 1e9 + apps.Count, OpenAppType, info.label, info.target, null,
                    info.target is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(t) : null, null, false, false));
                running[id] = group.ToList();
            }
        }
        // keep the order they first appeared in, so icons don't shuffle when a window changes
        var keep = _openApps.Where(a => apps.Any(n => n.Id == a.Id)).Select(a => apps.First(n => n.Id == a.Id));
        var ordered = keep.Concat(apps.Where(n => _openApps.All(a => a.Id != n.Id))).ToList();
        if (ordered.Select(a => a.Id).SequenceEqual(_openApps.Select(a => a.Id))) return;
        _openApps = ordered;
        _items = [.. _pinned, .. _openApps];
        var px = IconPixelSize;
        foreach (var app in _openApps)
        {
            _size.TryAdd(app.Id, _m.IconSize);
            if (!_renderer.HasIcon(app.Id)) LoadIcon(app, px);
        }
        foreach (var gone in _size.Keys.Except(_items.Select(i => i.Id)).ToList()) _size.Remove(gone);
        Kick();
    }

    /// <summary>"Keep on dock": pin an open app where the pinned items end.</summary>
    private void PinOpenApp(DockItem app)
    {
        if (app.Target is not { } target) return;
        _openApps.RemoveAll(a => a.Id == app.Id);
        AddItems([new NewItem(Shortcuts.TypeFor(target), app.Label, target, StartIn: app.StartIn)], _pinned.Count);
    }

    /// <summary>Fetch every item's icon again (after a folder's icon was changed, for example).</summary>
    public void RefreshIcons()
    {
        var iconPx = IconPixelSize;
        foreach (var item in _items)
            LoadIcon(item, iconPx);
        Kick();
    }

    private int IconPixelSize => (int)Math.Min(256, MathF.Ceiling(_m.MagnifiedSize / 32) * 32);

    private void LoadIcon(DockItem item, int iconPx)
    {
        var px = ShellIcons.ForItem(item.IconPath, item.Target, Math.Max(iconPx, 128));
        if (px is not null) _renderer.SetIcon(item.Id, px.Width, px.Height, px.Bgra);
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

        var hideGoal = HideGoal(out var hidePending);
        if (_hide != hideGoal)
        {
            var wasTucked = Tucked;
            var step = dt / 0.22f;   // a little under a quarter of a second, either way
            _hide = MathF.Abs(hideGoal - _hide) <= step ? hideGoal : _hide + MathF.Sign(hideGoal - _hide) * step;
            ApplyHideOffset();
            if (wasTucked && !Tucked) SetExpanded(true, force: true);   // coming back: the whole dock takes the pointer again
            moving = true;
        }

        Render();

        var toastLive = _toast is not null && Environment.TickCount64 < _toastUntil;
        if (!moving && !_dragging && !_movingDock && _pointerX is null && !toastLive && !hidePending)
        {
            _clock.Stop();
            _animating = false;
            _toast = null;
            SetExpanded(false);
            Render();
        }
    }

    // ------------------------------------------------------------------ auto-hide

    // ------------------------------------------------------------------ keeping windows clear

    private int _reserved;          // pixels of screen edge this dock holds as an app bar (0 = none)
    private bool _appBar;
    private RECT _appBarRect;

    private bool ReservesSpace => _behavior.ReserveSpace && !_behavior.AutoHide && _placement.Edge != DockEdge.Float;

    /// <summary>
    /// Hold (or let go of) the dock's strip of the screen edge, the way the taskbar does, so maximised
    /// windows stop at the band. Only the band's thickness is held, not the room magnified icons use.
    /// </summary>
    private void UpdateAppBar()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = WM_APPBAR };
        if (!ReservesSpace)
        {
            if (!_appBar) return;
            SHAppBarMessage(ABM_REMOVE, ref data);
            (_appBar, _reserved, _appBarRect) = (false, 0, default);
            Place();
            return;
        }
        if (!_appBar) { SHAppBarMessage(ABM_NEW, ref data); _appBar = true; }

        var band = (int)MathF.Ceiling(_m.BandHeight);
        var (l, t, r, b) = ((int)_work.X, (int)_work.Y, (int)_work.Right, (int)_work.Bottom);
        (data.uEdge, data.rc) = _placement.Edge switch
        {
            DockEdge.Top => (ABE_TOP, new RECT { Left = l, Top = t, Right = r, Bottom = t + band }),
            DockEdge.Left => (ABE_LEFT, new RECT { Left = l, Top = t, Right = l + band, Bottom = b }),
            DockEdge.Right => (ABE_RIGHT, new RECT { Left = r - band, Top = t, Right = r, Bottom = b }),
            _ => (ABE_BOTTOM, new RECT { Left = l, Top = b - band, Right = r, Bottom = b }),
        };
        if (data.rc.Equals(_appBarRect)) return;   // nothing changed: don't stir up another work-area change
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        SHAppBarMessage(ABM_SETPOS, ref data);
        _appBarRect = data.rc;
        _reserved = band;
    }

    private void ReleaseAppBar()
    {
        if (!_appBar) return;
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        SHAppBarMessage(ABM_REMOVE, ref data);
        _appBar = false;
    }

    private bool AutoHides => _behavior.AutoHide && _placement.Edge != DockEdge.Float;

    /// <summary>Hidden all the way past the screen edge (only a thin strip at the edge still notices the pointer).</summary>
    private bool Tucked => AutoHides && _hide >= 1;

    /// <summary>A click on the edge strip brings a tucked-away dock back straight away, without the wait.</summary>
    private void RevealNow()
    {
        _edgeSince = long.MinValue / 2;
        Kick();
    }

    private void SaveBehavior(DockBehavior b)
    {
        _behavior = b;
        _store.SetBehavior(_containerId, b);
    }

    /// <summary>
    /// Where the hide animation is heading: 1 (tucked away) once the pointer has been gone for a moment
    /// and nothing needs the dock (a drag, a press, previews, a message, an empty dock's hint), else 0.
    /// <paramref name="pending"/> keeps the frame loop alive through the delay.
    /// </summary>
    private float HideGoal(out bool pending)
    {
        pending = false;
        if (!AutoHides) return 0;
        if (Tucked && _pointerX is not null && !_dragging && !_movingDock)
        {
            // tucked away: come back only once the pointer has rested at the edge for a moment
            pending = true;
            if (Environment.TickCount64 - _edgeSince < _behavior.RevealDelayMs) return 1;
        }
        var away = _pointerX is null && !_dragging && !_movingDock && !_pressed && _preview?.ItemId is null && _items.Count > 0
                   && !(_toast is not null && Environment.TickCount64 < _toastUntil);
        if (!away) return 0;
        pending = _hide < 1;
        return Environment.TickCount64 - _pointerLeftAt < HideDelayMs ? 0 : 1;
    }

    /// <summary>Slide the finished picture toward the screen edge (composition only, no redraw).</summary>
    private void ApplyHideOffset()
    {
        var e = _hide * _hide * (3 - 2 * _hide);   // ease in and out
        var ext = _lastExtent.W > 0 ? _lastExtent : _lastBackground;
        var d = (_frame.Thickness - ext.Y + 2 * _scale) * e;   // the band and its decorations, all the way past the edge
        var (x, y) = _frame.Edge switch
        {
            DockEdge.Top => (0f, -d),
            DockEdge.Left => (-d, 0f),
            DockEdge.Right => (d, 0f),
            _ => (0f, d),
        };
        _renderer.SetOffset(x, y);
    }

    private void SetBehavior(DockBehavior b)
    {
        SaveBehavior(b);
        if (!b.Previews) HidePreview();
        _pointerLeftAt = Environment.TickCount64;
        Kick();
    }

    // ------------------------------------------------------------------ window previews

    /// <summary>The item under the pointer, if it has open windows.</summary>
    private string? RunningItemUnderPointer()
    {
        if (_pointerX is not float x || _pointerY is not float y || _dragging || _pressed || _movingDock) return null;
        var (rects, _) = CurrentRects();
        var hit = DockLayout.HitTest(rects, x, y, _m.Spacing / 2);
        return hit >= 0 && _running.ContainsKey(_items[hit].Id) ? _items[hit].Id : null;
    }

    private void TrackHover()
    {
        if (Tucked) { _hoverItem = null; return; }   // no previews for icons nobody can see yet
        var id =_behavior.Previews ? RunningItemUnderPointer() : null;
        if (id != _hoverItem) { _hoverItem = id; _hoverSince = Environment.TickCount64; }
        if (id is not null && id != _previewMuted) _previewMuted = null;   // moved to another icon: previews are back
        if (_hoverItem is not null || _preview?.ItemId is not null) _previewTimer.Start();
    }

    /// <summary>
    /// Every 80 ms while previews are possible: show them after the pointer rests on a running icon,
    /// switch at once when it moves to another, and hide them shortly after it leaves both the icon
    /// and the previews (the short grace lets it cross the gap between them).
    /// </summary>
    private void PreviewTick()
    {
        var now = Environment.TickCount64;
        var showing = _preview?.ItemId;
        if (_menuOpen || (_hoverItem is not null && _hoverItem == _previewMuted))
        {
            HidePreview();
            return;
        }
        if (_hoverItem is { } id && _running.TryGetValue(id, out var wins) && !_dragging && !_pressed)
        {
            _previewLeftAt = -1;
            if (showing != id && (showing is not null || now - _hoverSince >= PreviewDelayMs)) ShowPreview(id, wins);
            return;
        }
        if (showing is null) { _previewTimer.Stop(); return; }
        GetCursorPos(out var p);
        if (_preview!.Contains(p.X, p.Y)) { _previewLeftAt = -1; return; }
        if (_previewLeftAt < 0) _previewLeftAt = now;
        else if (now - _previewLeftAt >= PreviewGraceMs) HidePreview();
    }

    private void ShowPreview(string id, List<TaskWindow> wins)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        var (rects, _) = CurrentRects();
        var r = _frame.ToWindow(rects[i]);
        if (_preview is null)
        {
            _preview = new PreviewWindow();
            _preview.Acted += () => TaskWatcher.Shared.Refresh();
        }
        _previewKey = PreviewKey(wins);
        _preview.ShowFor(id, wins, _foreground, new System.Windows.Rect(_winX + r.X, _winY + r.Y, r.W, r.H),
            new System.Windows.Rect(_work.X, _work.Y, _work.W, _work.H), _scale, _placement.Edge);
        _previewLeftAt = -1;
        Render();   // the name label gives way to the previews
    }

    private string PreviewKey(List<TaskWindow> wins) => string.Join(",", wins.Select(w => w.Hwnd)) + "|" + _foreground;

    /// <summary>After a window change: rebuild open previews if that app's windows changed, or hide them when it has none.</summary>
    private void RefreshPreview()
    {
        if (_preview?.ItemId is not { } id) return;
        if (!_running.TryGetValue(id, out var wins)) { HidePreview(); return; }
        if (PreviewKey(wins) != _previewKey) ShowPreview(id, wins);
    }

    private void HidePreview()
    {
        if (_preview?.ItemId is null) return;
        _preview.HideNow();
        _previewLeftAt = -1;
        _pointerLeftAt = Environment.TickCount64;   // an auto-hiding dock waits a moment before tucking away
        Kick();
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
        bg = WithRoom(bg);

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
            icons.Add(new IconDraw(draggedId, _frame.ToWindow(new RectF(px - s / 2, py - s / 2, s, s)), IsRemoveGesture() ? 0.4f : 0.85f, false, NoReflection: true));
        }

        LabelDraw? label = null;
        var gap = 8 * _scale;
        if (_toast is not null && Environment.TickCount64 < _toastUntil)
            label = LabelAt(_toast, center, bg.Y - gap);
        else if (order.Count == 0 && !_dragging && HintVisible)
            label = LabelAt(_frame.Vertical ? "Drop apps here" : "Drop apps here and they'll stay put.", center, bg.Y - gap);
        else if (!_dragging && !_movingDock && _pointerX is float hx && _pointerY is float hy)
        {
            var hit = DockLayout.HitTest(rects, hx, hy, _m.Spacing / 2);
            if (hit >= 0 && order[hit].Id != _preview?.ItemId)
            {
                var item = order[hit];
                var text = _running.TryGetValue(item.Id, out var w) && w.Count > 1 ? $"{item.Label} · {w.Count} windows" : item.Label;
                label = LabelAt(text, rects[hit].CenterX, rects[hit].Y - 10 * _scale);
            }
        }

        var (decor, extent) = Decorations(bg);
        var attach = Attachments.Select(a => a.ToDraw()).ToList();
        _renderer.Draw(new DockScene(_frame.ToWindow(bg), _appearance.RadiusFor(bg.H), icons, indicators, label, _scale,
            _frame.Edge, _appearance.Shape == DockShapes.Leaf ? BandShape.Leaf : BandShape.Rounded,
            Ears: Theme?.Ears == true && _appearance.Opacity > 0, Snow: Theme?.Snow == true && _appearance.Opacity > 0, Decor: decor,
            Attach: attach, Reflection: _appearance.Reflection / 100f), _style);
        _lastBackground = bg;
        var grew = extent != _lastExtent;
        _lastExtent = extent;
        // at rest the window region hugs the band and its decorations; follow them when they change
        // (a new theme, an item added or removed) so nothing gets clipped
        if (grew && !_expanded) SetExpanded(false, force: true);
    }

    /// <summary>
    /// The theme's decorations for a band at <paramref name="bg"/> (dock space), in window pixels, and the
    /// dock-space box that holds the band plus everything that sticks out of it (leaf tips, ears, pictures).
    /// An invisible dock (opacity 0) shows no decorations.
    /// </summary>
    /// <summary>The theme's cups, rings and moons (only on rounded docks you can see).</summary>
    private IEnumerable<AttachInfo> Attachments =>
        _appearance.Opacity > 0 && _appearance.Shape == DockShapes.Rounded && Theme is { } t ? t.Attach : [];

    /// <summary>The band, lengthened at the ends where the theme's attachments need the room.</summary>
    private RectF WithRoom(RectF bg)
    {
        if (!Attachments.Any() || Theme is not { } t) return bg;
        float start = t.RoomStart * bg.H, end = t.RoomEnd * bg.H;
        return new RectF(bg.X - start, bg.Y, bg.W + start + end, bg.H);
    }

    private (List<DecorDraw> decor, RectF extent) Decorations(RectF bg)
    {
        var list = new List<DecorDraw>();
        float x0 = bg.X, y0 = bg.Y, x1 = bg.Right, y1 = bg.Bottom;
        if (_appearance.Shape == DockShapes.Leaf)
        {
            x0 -= DockRenderer.LeafTip * bg.H;
            x1 += (DockRenderer.LeafTip + 0.36f) * bg.H;   // tip plus the stem
            y0 -= 0.08f * bg.H;
        }
        foreach (var a in Attachments)
        {
            // how far each one reaches past the band (matches the renderer's geometry)
            float H = bg.H, r = a.Size * H;
            switch (a.Kind)
            {
                case "cup": y0 = MathF.Min(y0, bg.Y + 0.02f * H - 0.45f * r - 1.28f * r); break;
                case "ring":
                {
                    y0 = MathF.Min(y0, bg.Y - 0.25f * r - 1.15f * r);
                    var cx = a.From == "end" ? bg.Right - a.Along * H : bg.X + a.Along * H;
                    var reach = 1.95f * r + 0.2f * r + 3;   // the ring's half-width plus its rounded, outlined end
                    x0 = MathF.Min(x0, cx - reach);
                    x1 = MathF.Max(x1, cx + reach);
                    break;
                }
                case "moon":
                    if (a.From == "end") x1 = MathF.Max(x1, bg.Right + 1.3f * r + 2);
                    else x0 = MathF.Min(x0, bg.X - 1.3f * r - 2);
                    y0 = MathF.Min(y0, bg.Y + 0.46f * H - r - 2);
                    y1 = MathF.Max(y1, bg.Y + 0.46f * H + r + 2);
                    break;
            }
        }
        if (_appearance.Opacity > 0 && Theme is { } theme)
        {
            if (theme.Ears) y0 = MathF.Min(y0, bg.Y - (DockRenderer.EarHeight + 0.04f) * bg.H);
            foreach (var d in theme.Decor)
            {
                var r = DockThemeCatalog.Place(d, bg);
                x0 = MathF.Min(x0, r.X); y0 = MathF.Min(y0, r.Y);
                x1 = MathF.Max(x1, r.Right); y1 = MathF.Max(y1, r.Bottom);
                var w = _frame.ToWindow(r);
                list.Add(new DecorDraw("theme:" + d.Sprite, w.X + w.W / 2, w.Y + w.H / 2, r.W, r.H,
                    DockThemeCatalog.Orient(_frame.Edge, d.Upright, d.Mirror), d.Behind));
            }
        }
        y1 = MathF.Min(y1, _frame.Thickness);
        y0 = MathF.Max(y0, 0);
        return (list, new RectF(x0, y0, x1 - x0, y1 - y0));
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
        if (expanded && !Tucked)   // while it waits at the edge, the hidden dock mustn't catch clicks meant for what's behind it
            r = new RectF(0, 0, _frame.Length, t);
        else if (AutoHides && _hide >= 1)
        {
            var e = _lastExtent.W > 0 ? _lastExtent : bg;
            var strip = MathF.Max(2, MathF.Round(2 * _scale));   // touch the edge here to bring the dock back
            r = new RectF(e.X, t - strip, e.W, strip);
        }
        else
        {
            var e = _lastExtent.W > 0 ? _lastExtent : bg;   // band plus decorations (a leaf's tips, ears, pictures)
            r = new RectF(e.X - 2, e.Y - 2, e.W + 4, t - e.Y + 2);
            // an empty dock also keeps its "Drop apps here" hint visible: beside a side dock, above a horizontal one
            if (_items.Count == 0 && HintVisible && _frame.Vertical)
                r = Union(r, new RectF(bg.X - 2, 0, bg.W + 4, t));
            else if (_items.Count == 0 && HintVisible)
                r = Union(r, new RectF(bg.CenterX - 200 * _scale, bg.Y - 44 * _scale, 400 * _scale, t - bg.Y + 44 * _scale));
        }
        var win = _frame.ToWindow(r);
        var rgn = expanded ? CreateRectRgn(0, 0, w, h)
            : CreateRectRgn((int)MathF.Floor(win.X), (int)MathF.Floor(win.Y), (int)MathF.Ceiling(win.Right), (int)MathF.Ceiling(win.Bottom));
        SetWindowRgn(_hwnd, rgn, true);   // the system owns rgn now
    }

    // ------------------------------------------------------------------ empty-dock hint

    /// <summary>While the hint is up: its few seconds haven't run out, or the pointer is resting on the empty dock.</summary>
    private bool HintVisible => _items.Count == 0 && (Environment.TickCount64 < _hintUntil || _pointerX is not null);

    private void ShowHint()
    {
        _hintUntil = Environment.TickCount64 + HintMs;
        _hintTimer.Stop();
        _hintTimer.Interval = TimeSpan.FromMilliseconds(HintMs);
        _hintTimer.Start();
    }

    /// <summary>Time's up, or the user clicked into another window: hide the hint and give the space back.</summary>
    private void HintExpired()
    {
        _hintTimer.Stop();
        _hintUntil = 0;
        if (_items.Count > 0 || _renderer is null) return;
        Render();
        if (!_expanded) SetExpanded(false, force: true);
    }

    private static RectF Union(RectF a, RectF b)
    {
        float x = MathF.Min(a.X, b.X), y = MathF.Min(a.Y, b.Y);
        return new RectF(x, y, MathF.Max(a.Right, b.Right) - x, MathF.Max(a.Bottom, b.Bottom) - y);
    }

    /// <summary>A short message in the dock's speech bubble (for news from the app, like an update being ready).</summary>
    public void Notify(string text) => Toast(text);

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
                if ((_movingDock || _pressed) && GetKeyState(VK_LBUTTON) >= 0)
                {
                    // the button is up but we never heard it released: stop here instead of following the pointer
                    if (_movingDock) MoveDockToCursor(save: true);
                    _dragging = _movingDock = _pressed = false;
                    _pressIndex = -1;
                    if (GetCapture() == _hwnd) ReleaseCapture();
                    Kick();
                    return 0;
                }
                if (_movingDock) { MoveDockToCursor(save: false); return 0; }
                // back on the dock after really leaving it (not just the menu taking the pointer): previews are back
                if (_pointerX is null && Environment.TickCount64 - Math.Max(_pointerLeftAt, _menuClosedAt) > 400) _previewMuted = null;
                var (x, y) = DockPoint(l);
                if (_pointerX is null && Tucked) _edgeSince = Environment.TickCount64;   // the wait at the edge starts now
                _pointerX = x;
                _pointerY = y;
                var past = MathF.Abs(x - _pressX) > DragThreshold * _scale || MathF.Abs(y - _pressY) > DragThreshold * _scale;
                if (_pressed && past && !_dragging)
                {
                    if (_pressOnEmpty) StartMovingDock();
                    else if (_pressIndex >= 0 && !IsOpenApp(_items[_pressIndex])) { _dragging = true; HidePreview(); }
                }
                TrackHover();
                Kick();
                return 0;
            }

            case WM_MOUSELEAVE:
                _tracking = false;
                if (GetCapture() != _hwnd)
                {
                    _pointerX = _pointerY = null;
                    _pointerLeftAt = Environment.TickCount64;
                    if (_items.Count == 0)
                    {
                        _hintUntil = Math.Max(_hintUntil, Environment.TickCount64 + 2500);
                        _hintTimer.Stop();
                        _hintTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _hintUntil - Environment.TickCount64));
                        _hintTimer.Start();
                    }
                    TrackHover();
                    Kick();
                }
                return 0;

            case WM_LBUTTONDOWN:
            {
                if (Tucked) { RevealNow(); return 0; }
                var (x, y) = DockPoint(l);
                var (rects, _) = CurrentRects();
                _pressIndex = DockLayout.HitTest(rects, x, y, _m.Spacing / 2);
                _pressOnEmpty = AltDown || _pressIndex < 0;   // Alt+drag moves the dock from anywhere
                _pressX = x;
                _pressY = y;
                _pressed = true;
                HidePreview();
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

            case WM_RBUTTONDOWN:
                _previewMuted = _hoverItem;
                HidePreview();
                return 0;

            case WM_RBUTTONUP:
            {
                if (Tucked) { RevealNow(); return 0; }
                _previewMuted = _hoverItem;
                var (x, y) = DockPoint(l);
                ShowMenu(x, y);
                return 0;
            }

            case WM_DROPFILES:
                OnDropFiles(w);
                return 0;

            case WM_APPBAR when (uint)w == ABN_POSCHANGED:   // another bar (the taskbar) moved: find our place again
                _appBarRect = default;
                Place();
                return 0;

            case WM_DPICHANGED:
            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE:   // the work area moves when the taskbar changes size/position
                if ((uint)w == 0x0014) Looks.WallpaperWatcher.Shared.Check();   // SPI_SETDESKWALLPAPER: a new wallpaper
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
        var newIndex = Math.Min(DockLayout.InsertIndex(rest, _pointerX ?? _pressX), _pinned.Count - 1);   // pinned items stay before open apps
        if (newIndex != index) _store.MoveItem(item.Id, newIndex);
        Reload();
    }

    private bool IsRemoveGestureAt(float? y) => y is float v && v < _frame.Thickness - _m.BandHeight - RemoveDistance * _scale;

    private void LaunchOrActivate(DockItem item, bool forceNew)
    {
        forceNew |= item.AllowMultiple;
        if (!forceNew && _running.TryGetValue(item.Id, out var windows) && windows.Count > 0)
        {
            Tasks.Activate(windows);
            TaskWatcher.Shared.Refresh();
            return;
        }
        var error = Tasks.Launch(item, forceNew);
        if (error is not null) Toast(error);
    }

    private DropTarget? _drop;

    /// <summary>Things dropped on the dock: files, folders, or Start menu apps (shell:AppsFolder\...), at a screen point.</summary>
    private void OnDrop(IReadOnlyList<string> targets, int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        ScreenToClient(_hwnd, ref pt);
        var (x, _) = _frame.ToDock(pt.X, pt.Y);
        var rest = DockLayout.RestCenters(_items.Count, _m, Center(_items.Count));
        var items = targets.Select(t => t.StartsWith(AppIds.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            ? new NewItem(ItemTypes.Program, AppIds.DisplayName(t[AppIds.AppsFolderPrefix.Length..]) ?? "App", t)
            : Shortcuts.FromDroppedPath(t)).ToList();
        AddItems(items, DockLayout.InsertIndex(rest, x));
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
        HidePreview();
        var (rects, _) = CurrentRects();
        var hit = DockLayout.HitTest(rects, x, y, _m.Spacing / 2);
        var menu = CreatePopupMenu();
        var position = CreatePopupMenu();
        const int Remove = 1, OpenLocation = 2, AddApp = 3, AddFolder = 4, AddFile = 12, Edit = 5, NewDock = 6, RemoveDock = 7, Looks = 8, Appearance = 9, Quit = 11, Previews = 14, KeepOnDock = 16, OpenApps = 17, Reserve = 18, WidgetBase = 40;
        const int PosBottom = 20, PosTop = 21, PosLeft = 22, PosRight = 23, PosFloat = 24, PosTip = 25;
        const int HideOff = 30, HideTip = 31, HideBase = 32;
        if (hit >= 0 && IsOpenApp(_items[hit]))
        {
            AppendMenu(menu, _items[hit].Target is null ? MF_STRING | MF_GRAYED : MF_STRING, KeepOnDock, "&Keep on dock");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
        }
        else if (hit >= 0)
        {
            AppendMenu(menu, MF_STRING, Edit, "&Edit name, location and icon…");
            AppendMenu(menu, MF_STRING, OpenLocation, "&Open file location");
            AppendMenu(menu, MF_STRING, Remove, "&Remove from dock");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
        }
        AppendMenu(menu, MF_STRING, AddApp, "&Add an app…");
        AppendMenu(menu, MF_STRING, AddFile, "Add a f&ile…");
        AppendMenu(menu, MF_STRING, AddFolder, "Add a &folder…");
        var widgetMenu = CreatePopupMenu();
        for (var i = 0; i < DesktopWidgetKinds.All.Length; i++) AppendMenu(widgetMenu, MF_STRING, WidgetBase + i, DesktopWidgetKinds.All[i].Name);
        AppendMenu(menu, MF_POPUP, widgetMenu, "Add a desktop &widget");
        AppendMenu(menu, MF_SEPARATOR, 0, null);

        uint Check(DockEdge e) => MF_STRING | (_placement.Edge == e ? MF_CHECKED : 0);
        AppendMenu(position, Check(DockEdge.Bottom), PosBottom, "&Bottom");
        AppendMenu(position, Check(DockEdge.Top), PosTop, "&Top");
        AppendMenu(position, Check(DockEdge.Left), PosLeft, "&Left");
        AppendMenu(position, Check(DockEdge.Right), PosRight, "&Right");
        AppendMenu(position, Check(DockEdge.Float), PosFloat, "&Floating");
        AppendMenu(position, MF_SEPARATOR, 0, null);
        AppendMenu(position, MF_STRING | MF_GRAYED, PosTip, "Tip: drag an empty part of the dock (or Alt+drag) to move it anywhere");
        AppendMenu(menu, MF_POPUP, position, "&Position");
        var hideMenu = CreatePopupMenu();
        AppendMenu(hideMenu, MF_STRING | (_behavior.AutoHide ? 0 : MF_CHECKED), HideOff, "&Off");
        AppendMenu(hideMenu, MF_SEPARATOR, 0, null);
        AppendMenu(hideMenu, MF_STRING | MF_GRAYED, HideTip, "On: rest the pointer at the screen edge to bring it back");
        for (var i = 0; i < RevealDelays.Length; i++)
            AppendMenu(hideMenu, MF_STRING | (_behavior.AutoHide && _behavior.RevealDelayMs == RevealDelays[i].Ms ? MF_CHECKED : 0), HideBase + i, RevealDelays[i].Menu);
        AppendMenu(menu, MF_POPUP | (_placement.Edge == DockEdge.Float ? MF_GRAYED : 0), hideMenu,
            _placement.Edge == DockEdge.Float ? "Auto-&hide (dock it to a screen edge first)" : "Auto-&hide");
        AppendMenu(menu, MF_STRING | (_behavior.Previews ? MF_CHECKED : 0), Previews, "Window pre&views");
        AppendMenu(menu, MF_STRING | (ShowsOpenApps ? MF_CHECKED : 0), OpenApps, "Show open apps &too");
        AppendMenu(menu, _placement.Edge == DockEdge.Float || _behavior.AutoHide ? MF_STRING | MF_GRAYED
            : MF_STRING | (_behavior.ReserveSpace ? MF_CHECKED : 0), Reserve, "Keep windows &clear of the dock");
        AppendMenu(menu, MF_STRING, Appearance, "Dock appearan&ce…");
        AppendMenu(menu, MF_STRING, NewDock, "&New dock");
        if (!IsMain) AppendMenu(menu, MF_STRING, RemoveDock, "Remove &this dock…");
        AppendMenu(menu, MF_STRING, Looks, "&Look packs: cursors, trails, folder icons…");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, Quit, "&Quit AmperDock");

        var (wx, wy) = _frame.ToWindow(x, y);
        var pt = new POINT { X = (int)wx, Y = (int)wy };
        ClientToScreen(_hwnd, ref pt);
        SetForegroundWindow(_hwnd);   // required so the menu closes when you click elsewhere
        _menuOpen = true;             // the menu runs its own message loop; the preview timer still ticks inside it
        int cmd;
        try { cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, 0); }
        finally { _menuOpen = false; _menuClosedAt = Environment.TickCount64; }
        DestroyMenu(menu);            // also destroys the submenu

        switch (cmd)
        {
            case KeepOnDock when hit >= 0 && IsOpenApp(_items[hit]):
                PinOpenApp(_items[hit]);
                break;
            case Reserve:
                SetBehavior(_behavior with { ReserveSpace = !_behavior.ReserveSpace });
                _appBarRect = default;
                Place();
                Toast(_behavior.ReserveSpace ? "Maximised windows now stop at the dock." : "Windows can go under the dock again.");
                break;
            case OpenApps:
                SetBehavior(_behavior with { ShowOpenApps = !ShowsOpenApps });
                TaskWatcher.Shared.Refresh();
                RefreshTasks();
                break;
            case Remove when hit >= 0 && !IsOpenApp(_items[hit]):
                var item = _items[hit];
                _store.RemoveItem(item.Id);
                Reload();
                Toast($"Removed {item.Label}");
                break;
            case OpenLocation when hit >= 0 && !IsOpenApp(_items[hit]):
                var t = _items[hit].Target;
                if (t is not null && (File.Exists(t) || Directory.Exists(t)))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t}\"") { UseShellExecute = true })?.Dispose();
                break;
            case Edit when hit >= 0 && !IsOpenApp(_items[hit]):
                EditItem(_items[hit]);
                break;
            case AddApp:
                AddFromAppList(after: hit);
                break;
            case AddFile:
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
                _commands.OpenAppearance(this);
                break;
            case HideOff:
                SetBehavior(_behavior with { AutoHide = false });
                Toast("Auto-hide is off.");
                break;
            case >= HideBase when cmd < HideBase + RevealDelays.Length:
                var delay = RevealDelays[cmd - HideBase];
                SetBehavior(_behavior with { AutoHide = true, RevealDelayMs = delay.Ms });
                Toast(delay.Ms == 0 ? "Auto-hide is on: touch the screen edge to bring the dock back."
                    : $"Auto-hide is on: rest the pointer at the screen edge for {delay.Spoken} to bring the dock back.");
                break;
            case Previews:
                SetBehavior(_behavior with { Previews = !_behavior.Previews });
                Toast(_behavior.Previews ? "Rest the pointer on an open app to see its windows." : "Window previews are off.");
                break;
            case NewDock:
                _commands.NewDock(this);
                break;
            case >= WidgetBase when cmd < WidgetBase + DesktopWidgetKinds.All.Length:
                _commands.AddWidget(DesktopWidgetKinds.All[cmd - WidgetBase].Kind, this);
                break;
            case RemoveDock when !IsMain:
                _commands.RemoveDock(this);
                break;
            case Looks:
                _commands.OpenLooks();
                break;
            case Quit:
                System.Windows.Application.Current.Shutdown();
                break;
        }
    }

    private readonly Dictionary<string, Settings.ItemWindow> _editors = new();

    /// <summary>Open the item editor (one window per item; a second request brings it forward).</summary>
    private void EditItem(DockItem item)
    {
        if (_editors.TryGetValue(item.Id, out var open)) { open.Activate(); return; }
        var win = new Settings.ItemWindow(_store, item, saved =>
        {
            Reload();
            LoadIcon(saved, IconPixelSize);
            Kick();
            Toast($"Saved {saved.Label}");
        });
        _editors[item.Id] = win;
        win.Closed += (_, _) => _editors.Remove(item.Id);
        win.Show();
        win.Activate();
    }

    private Settings.AppPickerWindow? _appPicker;

    /// <summary>"&Add an app…": the Start menu's app list, Store apps included.</summary>
    private void AddFromAppList(int after)
    {
        if (_appPicker is not null) { _appPicker.Activate(); return; }
        var targets = _items.Where(i => i.Target is not null).Select(i => i.Target!);
        _appPicker = new Settings.AppPickerWindow(targets,
            apps => AddItems(apps.Select(a => new NewItem(Shortcuts.TypeFor(a.Target), a.Name, a.Target,
                StartIn: a.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(a.Target) : null)).ToList(),
                Math.Min(after >= 0 ? after + 1 : _items.Count, _items.Count)),
            () => AddFromPicker(folder: false, after: after));
        _appPicker.Closed += (_, _) => _appPicker = null;
        _appPicker.Show();
        _appPicker.Activate();
    }

    /// <summary>
    /// "Add a f&ile…" / "Add a &folder…": the standard Windows pickers. New items go after the
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
    private void AddPaths(IReadOnlyList<string> paths, int index) => AddItems(paths.Select(Shortcuts.FromDroppedPath).ToList(), index);

    private void AddItems(IReadOnlyList<NewItem> items, int index)
    {
        var already = _pinned.Where(i => i.Target is not null).Select(i => i.Target!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = items.Where(n => n.Target is null || !already.Contains(n.Target)).ToList();
        index = Math.Min(index, _pinned.Count);   // new items go among the pinned ones, never after the open apps
        if (fresh.Count == 0) { Toast(items.Count == 1 ? "That's already on the dock." : "Those are already on the dock."); return; }
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
        ReleaseAppBar();
        if (_drop is not null) DropTarget.Revoke(_hwnd);
        TaskWatcher.Shared.Changed -= _onTasks;
        Looks.WallpaperWatcher.Shared.Changed -= _onWallpaper;
        _previewTimer.Stop();
        _hintTimer.Stop();
        _preview?.Close();
        _clock.Dispose();
        s_hosts.Remove(_hwnd);
        _renderer.Dispose();
        DestroyWindow(_hwnd);
    }
}
