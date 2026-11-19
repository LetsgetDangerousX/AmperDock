using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AmperDock.Core;
using AmperDock.Shell;

namespace AmperDock.App.Dock;

/// <summary>
/// Live previews of an app's open windows, shown beside the dock when the pointer rests on its icon.
/// The pictures are DWM thumbnails: Windows draws each window's live contents straight into our
/// window (the documented API the taskbar uses), so they move while you watch and cost us nothing.
/// Click a preview to switch to that window; the ✕ (or a middle-click) closes it.
/// </summary>
public sealed class PreviewWindow : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumb, out SIZE size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, System.Text.StringBuilder s, int max);

    private const uint DWM_TNP_RECTDESTINATION = 1, DWM_TNP_OPACITY = 4, DWM_TNP_VISIBLE = 8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10;
    private const uint WM_CLOSE = 0x0010;

    private sealed record Shown(nint Hwnd, nint Thumb, FrameworkElement Area);

    private readonly List<Shown> _shown = new();
    private readonly StackPanel _cards = new();
    private nint _hwnd;
    private Rect _anchor;          // the icon, in screen pixels
    private Rect _work;            // the monitor's work area, in screen pixels
    private float _scale = 1;      // the dock monitor's pixels per DIP
    private DockEdge _edge;

    /// <summary>The dock item these previews belong to (null when hidden).</summary>
    public string? ItemId { get; private set; }

    /// <summary>After the user switched to or closed a window from here.</summary>
    public event Action? Acted;

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Window previews";
        SetResourceReference(BackgroundProperty, "Brush.Surface");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Sans");
        Content = new Border { Padding = new Thickness(6), Child = _cards };
        AutomationProperties.SetName(this, "Window previews");
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            var round = 2;   // DWMWCP_ROUND: Windows 11 rounds the corners (ignored on Windows 10)
            DwmSetWindowAttribute(_hwnd, 33, ref round, 4);
        };
        LayoutUpdated += (_, _) => { if (IsVisible) { Position(); UpdateThumbs(); } };
    }

    /// <summary>Show (or switch to) the previews for one item's windows, beside its icon.</summary>
    public void ShowFor(string itemId, IReadOnlyList<TaskWindow> windows, nint foreground, Rect anchorPx, Rect workPx, float scale, DockEdge edge)
    {
        ItemId = itemId;
        (_anchor, _work, _scale, _edge) = (anchorPx, workPx, scale, edge);
        Clear();
        if (_hwnd == 0) _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var vertical = edge is DockEdge.Left or DockEdge.Right;
        _cards.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        // keep the row on the screen: shrink the pictures when an app has lots of windows
        var room = (vertical ? workPx.Height : workPx.Width) / scale * 0.92;
        var maxW = Math.Clamp(room / Math.Max(1, windows.Count) - 20, 90, 230);
        var maxH = vertical ? Math.Clamp(room / Math.Max(1, windows.Count) - 44, 60, 140) : 140;

        foreach (var w in windows)
        {
            if (DwmRegisterThumbnail(_hwnd, w.Hwnd, out var thumb) != 0) continue;
            DwmQueryThumbnailSourceSize(thumb, out var src);
            var (tw, th) = Fit(src.cx, src.cy, maxW, maxH);
            var area = new Grid { Width = tw, Height = th };
            if (Shell.Native.IsIconic(w.Hwnd))
            {
                var note = new TextBlock { Text = "Minimised", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                note.SetResourceReference(StyleProperty, "Text.Muted");
                area.Children.Add(note);
            }
            _cards.Children.Add(Card(w.Hwnd, TitleOf(w.Hwnd), area, w.Hwnd == foreground, tw));
            _shown.Add(new Shown(w.Hwnd, thumb, area));
        }
        if (_shown.Count == 0) { HideNow(); return; }
        if (!IsVisible) Show();
        UpdateLayout();
        Position();
        UpdateThumbs();
    }

    private static (double w, double h) Fit(int sw, int sh, double maxW, double maxH)
    {
        if (sw <= 0 || sh <= 0) return (maxW, maxH * 0.6);
        var k = Math.Min(maxW / sw, maxH / sh);
        return (Math.Max(90, sw * k), Math.Max(50, sh * k));
    }

    private static string TitleOf(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private FrameworkElement Card(nint hwnd, string title, FrameworkElement area, bool front, double width)
    {
        var text = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "Text.Body");
        text.TextWrapping = TextWrapping.NoWrap;   // one line; the full title is in the tooltip
        var close = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), Focusable = false };
        close.SetResourceReference(StyleProperty, "Btn.Ghost");
        AutomationProperties.SetName(close, $"Close {title}");
        close.Click += (_, _) => CloseWindow(hwnd);
        var header = new DockPanel { Width = width, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
        header.Children.Add(close);
        header.Children.Add(text);

        var card = new Border
        {
            Padding = new Thickness(8), Margin = new Thickness(2), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true, ToolTip = title,
            Child = new StackPanel { Children = { header, area } },
        };
        if (front) card.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        else card.BorderBrush = Brushes.Transparent;
        AutomationProperties.SetName(card, title);
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) Switch(hwnd);
            else if (e.ChangedButton == MouseButton.Middle) CloseWindow(hwnd);
        };
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) Switch(hwnd); };
        return card;
    }

    private void Switch(nint hwnd)
    {
        Tasks.Focus(hwnd);
        HideNow();
        Acted?.Invoke();
    }

    private void CloseWindow(nint hwnd)
    {
        PostMessageW(hwnd, WM_CLOSE, 0, 0);   // asks politely: the app can still offer to save
        Acted?.Invoke();
    }

    /// <summary>Beside the icon, away from the screen edge, kept inside the work area.</summary>
    private void Position()
    {
        if (_hwnd == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = ActualWidth * dpi, h = ActualHeight * dpi, gap = 10 * _scale;
        double x, y;
        switch (_edge)
        {
            case DockEdge.Left: x = _anchor.Right + gap; y = _anchor.Top + _anchor.Height / 2 - h / 2; break;
            case DockEdge.Right: x = _anchor.Left - gap - w; y = _anchor.Top + _anchor.Height / 2 - h / 2; break;
            case DockEdge.Top: x = _anchor.Left + _anchor.Width / 2 - w / 2; y = _anchor.Bottom + gap; break;
            default: x = _anchor.Left + _anchor.Width / 2 - w / 2; y = _anchor.Top - gap - h; break;
        }
        x = Math.Clamp(x, _work.Left, Math.Max(_work.Left, _work.Right - w));
        y = Math.Clamp(y, _work.Top, Math.Max(_work.Top, _work.Bottom - h));
        SetWindowPos(_hwnd, -1 /* HWND_TOPMOST */, (int)Math.Round(x), (int)Math.Round(y), 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Tell Windows where each live picture goes (in our window's pixels).</summary>
    private void UpdateThumbs()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var s in _shown)
        {
            if (!s.Area.IsLoaded || PresentationSource.FromVisual(s.Area) is null) continue;
            var p = s.Area.TransformToAncestor(this).Transform(new Point(0, 0));
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT
                {
                    Left = (int)Math.Round(p.X * dpi), Top = (int)Math.Round(p.Y * dpi),
                    Right = (int)Math.Round((p.X + s.Area.ActualWidth) * dpi), Bottom = (int)Math.Round((p.Y + s.Area.ActualHeight) * dpi),
                },
                opacity = 255, fVisible = 1, fSourceClientAreaOnly = 0,
            };
            DwmUpdateThumbnailProperties(s.Thumb, ref props);
        }
    }

    private void Clear()
    {
        foreach (var s in _shown) DwmUnregisterThumbnail(s.Thumb);
        _shown.Clear();
        _cards.Children.Clear();
    }

    public void HideNow()
    {
        ItemId = null;
        Clear();
        Hide();
    }

    /// <summary>Is the given screen point (pixels) over this window?</summary>
    public bool Contains(int x, int y)
    {
        if (!IsVisible || _hwnd == 0 || !Win32.GetWindowRect(_hwnd, out var r)) return false;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    protected override void OnClosed(EventArgs e)
    {
        Clear();
        base.OnClosed(e);
    }
}
