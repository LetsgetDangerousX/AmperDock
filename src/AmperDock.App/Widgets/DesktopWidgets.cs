using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AmperDock.Core;

namespace AmperDock.App;

/// <summary>The desktop widgets you can add: a note, a clock and a countdown.</summary>
public static class DesktopWidgetKinds
{
    public static readonly (string Kind, string Name)[] All = [("note", "Note"), ("clock", "Clock"), ("countdown", "Countdown"), ("weather", "Weather"), ("mochi", "Mochi"),
        (MochiThingWidget.Post, "Mochi's scratch post"), (MochiThingWidget.Bowl, "Mochi's food bowl"), (MochiThingWidget.House, "Mochi's house"),
        (PalWidget.Bella, "Bella (Mochi's pal)"), (PalWidget.Bunny, "Bunny (Mochi's pal)"), (PalWidget.Dino, "Dino (Mochi's pal)")];
}

/// <summary>The colours a widget can wear: a soft gradient, the text colour on it, and an accent.</summary>
public static class WidgetColours
{
    public sealed record Look(string Name, Color Top, Color Bottom, Color Ink, Color Accent);

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly Dictionary<string, Look> All = new()
    {
        ["lilac"] = new("Lilac", C("#F1E8FF"), C("#DCCBFB"), C("#2A1F3D"), C("#9C7BE6")),
        ["pink"] = new("Pink", C("#FFEDF4"), C("#FFCFE2"), C("#3A1F2D"), C("#E86FA6")),
        ["mint"] = new("Mint", C("#E7FAF0"), C("#C4EED9"), C("#1F3A2E"), C("#3FA98A")),
        ["butter"] = new("Butter", C("#FFF8E1"), C("#FFE8AE"), C("#3A2E1A"), C("#E5A43A")),
        ["sky"] = new("Sky", C("#EAF5FF"), C("#CBE4FF"), C("#1A2A3D"), C("#4A86D8")),
        ["night"] = new("Night", C("#3B2D57"), C("#261C3B"), C("#F3ECFF"), C("#B497FF")),
    };

    /// <summary>A named colour, or any "#RRGGBB": a soft gradient from it, with dark or light text to suit.</summary>
    public static Look Get(string id)
    {
        if (All.TryGetValue(id, out var named)) return named;
        if (Controls.ColorPicker.Parse(id) is not { } c) return All["lilac"];
        var light = Luma(c) > 0.55;
        return new Look("Your colour", Mix(c, Colors.White, 0.35), c, light ? C("#2A1F3D") : C("#F7F2FF"), Mix(c, light ? C("#2A1F3D") : Colors.White, 0.35));
    }

    public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

/// <summary>A small "pick any colour" window around the app's colour picker. Returns "#RRGGBB", or null if cancelled.</summary>
public static class ColourDialog
{
    public static string? Ask(Window? owner, string title, string? start)
    {
        var picker = new Controls.ColorPicker();
        if (Controls.ColorPicker.Parse(start) is { } c) picker.Color = c;
        string? result = null;
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        win.SetResourceReference(Window.BackgroundProperty, "Brush.Bg");
        win.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
        win.SetResourceReference(Window.FontFamilyProperty, "Font.Sans");
        var ok = new Button { Content = "Use this colour", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Primary");
        ok.Click += (_, _) => { result = Controls.ColorPicker.Hex(picker.Color); win.DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Secondary");
        win.Content = new StackPanel
        {
            Margin = new Thickness(20), Width = 280,
            Children = { picker, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } } },
        };
        if (owner is { IsVisible: true }) win.Owner = owner;
        return win.ShowDialog() == true ? result : null;
    }
}

/// <summary>
/// Keeps the widgets on the desktop: loads them at start-up, adds and removes them, and saves every
/// change (position, colour, text) to the settings.
/// </summary>
public sealed class DesktopWidgetManager
{
    private readonly SettingsStore _store;
    private readonly List<WidgetWindow> _open = [];

    public DesktopWidgetManager(SettingsStore store) => _store = store;

    /// <summary>A widget was added, removed or changed (for the AmperDock window's list).</summary>
    public event Action? Changed;

    /// <summary>The widgets on the desktop.</summary>
    public IReadOnlyList<DesktopWidget> List() => Load();

    private List<DesktopWidget> Load() => DesktopWidget.ListFromJson(_store.GetSetting(DesktopWidget.SettingsKey));

    private void Save(List<DesktopWidget> list)
    {
        _store.SetSetting(DesktopWidget.SettingsKey, DesktopWidget.ListToJson(list));
        Changed?.Invoke();
    }

    public void Start()
    {
        foreach (var w in Load()) Open(w);
    }

    /// <summary>A new widget near the top right of the main screen, a little offset from the last one.</summary>
    public void Add(string kind)
    {
        var list = Load();
        var work = SystemParameters.WorkArea;
        var n = list.Count % 6;
        var (x, y) = (work.Right - 320 - n * 28, work.Top + 60 + n * 28);
        if (kind.StartsWith("mochi-") && list.FirstOrDefault(o => o.Kind == "mochi") is { } mochi)
        {
            // Mochi's things go on her row, spaced out beside her, so she has a short walk to them
            var things = list.Count(o => o.Kind.StartsWith("mochi-"));
            x = Math.Clamp(mochi.X + (things % 2 == 0 ? 1 : -1) * (300 + 180 * (things / 2)), work.Left, work.Right - 260);
            y = mochi.Y - 40;
        }
        var w = new DesktopWidget
        {
            Kind = kind,
            X = x, Y = y,
            Colour = kind switch { "note" => "pink", "countdown" => "mint", "weather" => "sky", "mochi" => "#FFFFFF", _ => "lilac" },
            Text = kind == "countdown" ? "Christmas" : "",
            Target = kind == "countdown" ? DesktopWidget.NextChristmas(DateTime.Now) : null,
        };
        list.Add(w);
        Save(list);
        Open(w);
    }

    /// <summary>Has the desktop got a widget of this kind already?</summary>
    public bool Has(string kind) => Load().Any(w => w.Kind == kind);

    /// <summary>Mochi's things on the desktop (post, bowl, house), of one kind or all of them.</summary>
    public IEnumerable<MochiThingWidget> Things(string? kind) =>
        _open.OfType<MochiThingWidget>().Where(t => t.IsLoaded && (kind is null || t.Kind == kind)).ToList();

    public IEnumerable<MochiWidget> Mochis() => _open.OfType<MochiWidget>().Where(m => m.IsLoaded).ToList();

    /// <summary>Mochi's pals on the desktop (including any out of sight in her house).</summary>
    public IEnumerable<PalWidget> Pals() => _open.OfType<PalWidget>().ToList();

    public void Update(DesktopWidget changed)
    {
        var list = Load();
        var i = list.FindIndex(w => w.Id == changed.Id);
        if (i < 0) return;
        list[i] = changed;
        Save(list);
    }

    public void Remove(string id)
    {
        Save(Load().Where(w => w.Id != id).ToList());
        if (_open.FirstOrDefault(w => w.Data.Id == id) is { } win)
        {
            _open.Remove(win);
            win.Close();
        }
    }

    private void Open(DesktopWidget w)
    {
        if (_open.Any(o => o.Data.Id == w.Id)) return;   // already on the desktop
        WidgetWindow win = w.Kind switch
        {
            "clock" => new ClockWidget(w, this),
            "countdown" => new CountdownWidget(w, this),
            "mochi" => new MochiWidget(w, this),
            MochiThingWidget.Post or MochiThingWidget.Bowl or MochiThingWidget.House => new MochiThingWidget(w, this),
            PalWidget.Bella or PalWidget.Bunny or PalWidget.Dino => new PalWidget(w, this),
            "weather" => new WeatherWidget(w, this),
            _ => new NoteWidget(w, this),
        };
        _open.Add(win);
        win.Show();
    }

    public void CloseAll()
    {
        foreach (var w in _open.ToList()) w.Close();
        _open.Clear();
    }
}

/// <summary>
/// A widget's window: a soft rounded card that sits on the desktop under your windows (owned by the
/// desktop, so Win+D leaves it showing), is dragged by its card, and has a right-click menu for its
/// colour and for removing it.
/// </summary>
public abstract class WidgetWindow : Window
{
    protected readonly DesktopWidgetManager Manager;
    public DesktopWidget Data { get; protected set; }
    protected readonly Border Card;
    private readonly DispatcherTimer _saveSoon;

    protected WidgetWindow(DesktopWidget data, DesktopWidgetManager manager, double width, double height)
    {
        Data = data;
        Manager = manager;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Left = data.X;
        Top = data.Y;
        SetResourceReference(FontFamilyProperty, "Font.Sans");

        Card = new Border
        {
            Margin = new Thickness(14), CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22, Color = Color.FromRgb(0x2A, 0x1F, 0x3D) },
        };
        UseBody(Card, width + 28, height + 28);
        MouseEnter += (_, _) => _grip.Opacity = 0.75;
        MouseLeave += (_, _) => _grip.Opacity = 0;
        Card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox or System.Windows.Controls.Primitives.TextBoxBase) return;
            if (e.ClickCount == 2) { OnDoubleClick(); return; }
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        LocationChanged += (_, _) => { if (!SavesMoves) return; _saveSoon!.Stop(); _saveSoon.Start(); };
        _saveSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _saveSoon!.Stop();
            Save(Data with { X = Left, Y = Top });
        }, Dispatcher);
        SourceInitialized += (_, _) => SitOnDesktop();
        Loaded += (_, _) => ApplyColour();
        // built fresh each time it opens, so what's on or off fits right now (e.g. "Exit" once Mochi's in her house)
        Card.ContextMenuOpening += (_, _) => Card.ContextMenu = BuildMenu();
        Action follow = () => { if (Data.Colour == WallpaperColour) ApplyColour(); };
        Looks.WallpaperWatcher.Shared.Changed += follow;
        Closed += (_, _) => Looks.WallpaperWatcher.Shared.Changed -= follow;
    }

    protected virtual void OnDoubleClick() { }

    // ---- resizing: drag any corner

    /// <summary>True: the whole widget scales evenly when resized (clock, countdown, Mochi). False: it just gets more room (note).</summary>
    protected virtual bool ScalesContent => true;

    private readonly Grid _frame = new();
    private readonly Path _grip = new()
    {
        Data = Geometry.Parse("M14,4 a1.6,1.6 0 1 0 0.01,0 M9,9 a1.6,1.6 0 1 0 0.01,0 M14,9 a1.6,1.6 0 1 0 0.01,0 M4,14 a1.6,1.6 0 1 0 0.01,0 M9,14 a1.6,1.6 0 1 0 0.01,0 M14,14 a1.6,1.6 0 1 0 0.01,0"),
        Fill = new SolidColorBrush(Color.FromRgb(0x7A, 0x6A, 0x96)), Opacity = 0, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 20, 20),
    };
    private double _designW, _designH;
    private FrameworkElement? _body;

    /// <summary>
    /// Put the widget's content in the window at its own size, with resize corners on top. Widgets that
    /// scale sit in a Viewbox, so everything in them grows and shrinks together without stretching.
    /// </summary>
    protected void UseBody(FrameworkElement body, double designW, double designH)
    {
        DetachBody();
        _body = body;
        (_designW, _designH) = (designW, designH);
        if (ScalesContent)
        {
            body.Width = designW;
            body.Height = designH;
            _frame.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = body });
        }
        else _frame.Children.Add(body);
        _frame.Children.Add(_grip);
        foreach (var corner in Corners()) _frame.Children.Add(corner);
        Width = Data.Width > 0 ? Data.Width : designW;
        Height = Data.Height > 0 ? Data.Height : designH;
        Content = _frame;
    }

    /// <summary>Free the content so it can be placed in a new layout (see <see cref="UseBody"/>).</summary>
    protected void DetachBody()
    {
        foreach (var child in _frame.Children.OfType<Viewbox>()) child.Child = null;
        _frame.Children.Clear();
        if (_body is not null) _body.Width = _body.Height = double.NaN;   // it may go inside a new layout at a different size
        _body = null;
    }

    private IEnumerable<System.Windows.Controls.Primitives.Thumb> Corners()
    {
        foreach (var (h, v) in new[] { (HorizontalAlignment.Left, VerticalAlignment.Top), (HorizontalAlignment.Right, VerticalAlignment.Top),
                                       (HorizontalAlignment.Left, VerticalAlignment.Bottom), (HorizontalAlignment.Right, VerticalAlignment.Bottom) })
        {
            var diagonal = (h == HorizontalAlignment.Left) == (v == VerticalAlignment.Top);
            var corner = new System.Windows.Controls.Primitives.Thumb
            {
                Width = 24, Height = 24, HorizontalAlignment = h, VerticalAlignment = v, Margin = new Thickness(8),
                Cursor = diagonal ? Cursors.SizeNWSE : Cursors.SizeNESW, Template = CornerTemplate(),
            };
            System.Windows.Automation.AutomationProperties.SetName(corner, "Resize");
            corner.DragDelta += (_, e) => ResizeBy(h, v, e.HorizontalChange, e.VerticalChange);
            corner.DragCompleted += (_, _) => Save(Data with { Width = Width, Height = Height, X = Left, Y = Top });
            yield return corner;
        }
    }

    /// <summary>An invisible but clickable square (alpha 1: transparent pixels of a see-through window don't take clicks).</summary>
    private static ControlTemplate CornerTemplate()
    {
        var t = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)));
        t.VisualTree = b;
        return t;
    }

    private void ResizeBy(HorizontalAlignment h, VerticalAlignment v, double dx, double dy)
    {
        var w = Width + (h == HorizontalAlignment.Right ? dx : -dx);
        var ht = Height + (v == VerticalAlignment.Bottom ? dy : -dy);
        if (ScalesContent)
        {
            var k = Math.Clamp(Math.Max(w / _designW, ht / _designH), 0.6, 3.5);   // keep its shape
            (w, ht) = (_designW * k, _designH * k);
        }
        else
        {
            w = Math.Clamp(w, _designW * 0.6, _designW * 4);
            ht = Math.Clamp(ht, _designH * 0.6, _designH * 4);
        }
        if (h == HorizontalAlignment.Left) Left += Width - w;
        if (v == VerticalAlignment.Top) Top += Height - ht;
        Width = w;
        Height = ht;
    }

    protected void Save(DesktopWidget changed)
    {
        Data = changed;
        Manager.Update(changed);
    }

    public const string WallpaperColour = "wallpaper";

    /// <summary>The card's colours: a named or chosen colour, or (for "wallpaper") the colours of the wallpaper behind it.</summary>
    protected WidgetColours.Look Colours
    {
        get
        {
            if (Data.Colour != WallpaperColour) return WidgetColours.Get(Data.Colour);
            var px = PresentationSource.FromVisual(this) is null ? new Point(Left, Top) : PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            if (Looks.WallpaperWatcher.Shared.PaletteFor(new RectF((float)px.X, (float)px.Y, 1, 1)) is not { } p) return WidgetColours.Get("lilac");
            Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
            return p.Dark
                ? new WidgetColours.Look("Wallpaper", C(p.Fill2), C(p.Fill), C("#F3ECFF"), C(p.Indicator))
                : new WidgetColours.Look("Wallpaper", C(p.Fill), C(p.Fill2), C("#2A1F3D"), C(p.Indicator));
        }
    }
    protected Brush InkBrush => new SolidColorBrush(Colours.Ink);

    /// <summary>Paint the card in the widget's colour; each widget repaints its own text too.</summary>
    protected virtual void ApplyColour()
    {
        var c = Colours;
        Card.Background = new LinearGradientBrush(c.Top, c.Bottom, 90);
        Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, c.Accent.R, c.Accent.G, c.Accent.B));
        Card.ContextMenu = BuildMenu();
    }

    /// <summary>Extra items a widget adds at the top of its right-click menu.</summary>
    protected virtual IEnumerable<object> MenuItems() => [];

    /// <summary>The colours offered in the Colour menu: (stored value, name, swatch top, swatch bottom).</summary>
    protected virtual IEnumerable<(string Value, string Name, Color Top, Color Bottom)> ColourChoices() =>
        new[] { (WallpaperColour, "Match my wallpaper", Color.FromRgb(0xB9, 0xA4, 0xFF), Color.FromRgb(0x7E, 0xD9, 0xAE)) }
            .Concat(WidgetColours.All.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Top, kv.Value.Bottom)));

    protected virtual string ColourMenuName => "_Colour";

    /// <summary>False for widgets drawn from pictures (Mochi), which have no colour to choose.</summary>
    protected virtual bool HasColourMenu => true;

    /// <summary>False while a widget moves itself (Mochi walking): its position is saved when it stops instead.</summary>
    protected bool SavesMoves { get; set; } = true;

    /// <summary>A submenu of colour swatches plus "Any colour…", calling <paramref name="pick"/> with the value chosen.</summary>
    protected MenuItem ColourMenu(string header, IEnumerable<(string Value, string Name, Color Top, Color Bottom)> choices, string? current, Action<string?> pick, bool automatic = false)
    {
        var menu = new MenuItem { Header = header };
        if (automatic)
        {
            var auto = new MenuItem { Header = "_Automatic (suits the card)", IsCheckable = true, IsChecked = current is null };
            auto.Click += (_, _) => pick(null);
            menu.Items.Add(auto);
        }
        var known = false;
        foreach (var (value, name, top, bottom) in choices)
        {
            var swatch = new Ellipse { Width = 14, Height = 14, Fill = new LinearGradientBrush(top, bottom, 90), Stroke = new SolidColorBrush(Color.FromRgb(0x9C, 0x7B, 0xE6)), StrokeThickness = 1 };
            var isCurrent = string.Equals(value, current, StringComparison.OrdinalIgnoreCase);
            known |= isCurrent;
            var mi = new MenuItem { Header = name, Icon = swatch, IsCheckable = true, IsChecked = isCurrent };
            mi.Click += (_, _) => pick(value);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var any = new MenuItem { Header = "_Any colour…", IsCheckable = true, IsChecked = current is not null && !known };
        any.Click += (_, _) => { if (ColourDialog.Ask(this, "Pick a colour", current) is { } hex) pick(hex); };
        menu.Items.Add(any);
        return menu;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in MenuItems()) menu.Items.Add(item);
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        if (HasColourMenu)
            menu.Items.Add(ColourMenu(ColourMenuName, ColourChoices(), Data.Colour, v => { if (v is not null) { Save(Data with { Colour = v }); ApplyColour(); } }));
        var remove = new MenuItem { Header = "_Remove widget" };
        remove.Click += (_, _) => Manager.Remove(Data.Id);
        menu.Items.Add(remove);
        return menu;
    }

    // ---- living on the desktop

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public nint hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    private void SitOnDesktop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtrW(hwnd, -20, (nint)((long)GetWindowLongPtrW(hwnd, -20) | 0x80));   // a tool window: not on the taskbar or in Alt+Tab
        var desktop = FindWindowW("Progman", null);
        if (desktop != 0) SetWindowLongPtrW(hwnd, -8, desktop);                            // owned by the desktop: Win+D leaves it showing
        HwndSource.FromHwnd(hwnd)?.AddHook(KeepAtBottom);
        SetWindowPos(hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
    }

    /// <summary>Stay under every window, even when clicked or typed in.</summary>
    private static nint KeepAtBottom(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x46 /* WM_WINDOWPOSCHANGING */)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(l);
            if ((wp.flags & 0x4 /* SWP_NOZORDER */) == 0)
            {
                wp.hwndInsertAfter = 1;
                Marshal.StructureToPtr(wp, l, false);
            }
        }
        return 0;
    }

    protected static TextBlock Text(string text, double size, FontWeight weight, Brush brush) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis };

    protected static Path Sparkle(double r, Brush fill) => new()
    {
        Fill = fill, Width = r * 2, Height = r * 2, Stretch = Stretch.Fill,
        Data = Geometry.Parse("M10,0 Q11.8,8.2 20,10 Q11.8,11.8 10,20 Q8.2,11.8 0,10 Q8.2,8.2 10,0 Z"),
    };
}

/// <summary>
/// A sticky note with washi tape across the top. Type straight into it; it saves as you go. The card
/// colour, text colour, font and text size are all yours to choose from its right-click menu.
/// </summary>
public sealed class NoteWidget : WidgetWindow
{
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly Rectangle _tape;
    private readonly Path _sparkle;
    private readonly DispatcherTimer _typing;

    /// <summary>Fonts for notes: the app's rounded font and the friendly ones every Windows 10/11 PC has.</summary>
    private static readonly (string Family, string Name)[] Fonts =
    [
        ("", "Nunito (the AmperDock font)"), ("Ink Free", "Ink Free (handwriting)"), ("Segoe Print", "Segoe Print (handwriting)"),
        ("Comic Sans MS", "Comic Sans"), ("Gabriola", "Gabriola (fancy)"), ("Segoe UI", "Segoe UI"), ("Cascadia Code", "Cascadia Code (typewriter)"),
    ];

    private static readonly (string Value, string Name)[] TextColours =
    [
        ("#2A1F3D", "Ink"), ("#FFFFFF", "White"), ("#5A2D82", "Plum"), ("#C2185B", "Rose"), ("#00796B", "Teal"), ("#1A3A6E", "Navy"), ("#6D4C41", "Cocoa"),
    ];

    private static readonly (double Size, string Name)[] Sizes = [(13, "Small"), (15, "Medium"), (18, "Large"), (22, "Extra large")];

    protected override bool ScalesContent => false;   // a bigger note means more room to write

    public NoteWidget(DesktopWidget data, DesktopWidgetManager manager) : base(data, manager, 230, 230)
    {
        _box = new TextBox
        {
            Text = data.Text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Margin = new Thickness(16, 30, 16, 16), Padding = new Thickness(0),
            Template = PlainTextTemplate(),   // the theme's text box turns dark when focused; a note keeps its own colours
        };
        System.Windows.Automation.AutomationProperties.SetName(_box, "Note");
        _placeholder = new TextBlock { Text = "Write a little note…", Margin = new Thickness(18, 30, 16, 16), IsHitTestVisible = false, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };
        _tape = new Rectangle { Width = 86, Height = 22, RadiusX = 3, RadiusY = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -8, 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-4), IsHitTestVisible = false };
        _sparkle = Sparkle(6, Brushes.White);
        _sparkle.HorizontalAlignment = HorizontalAlignment.Right;
        _sparkle.VerticalAlignment = VerticalAlignment.Top;
        _sparkle.Margin = new Thickness(0, 12, 16, 0);
        Card.Child = new Grid { Children = { _tape, _sparkle, _placeholder, _box } };
        _typing = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _typing!.Stop();
            Save(Data with { Text = _box.Text });
        }, Dispatcher);
        _box.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _typing.Stop();
            _typing.Start();
        };
        _placeholder.Visibility = data.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Closing += (_, _) => { if (_typing.IsEnabled) { _typing.Stop(); Save(Data with { Text = _box.Text }); } };
    }

    /// <summary>A text box that is only its text: no theme border, background or focus colours.</summary>
    private static ControlTemplate PlainTextTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.BackgroundProperty, Brushes.Transparent);
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        template.VisualTree = host;
        return template;
    }

    protected override IEnumerable<object> MenuItems()
    {
        var textColour = ColourMenu("_Text colour", TextColours.Select(t =>
        {
            var c = (Color)ColorConverter.ConvertFromString(t.Value);
            return (t.Value, t.Name, c, c);
        }), Data.TextColour, v => { Save(Data with { TextColour = v }); ApplyColour(); }, automatic: true);

        var font = new MenuItem { Header = "_Font" };
        foreach (var (family, name) in Fonts)
        {
            var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = (Data.Font ?? "") == family };
            if (family.Length > 0) mi.FontFamily = new FontFamily(family);
            var chosen = family;
            mi.Click += (_, _) => { Save(Data with { Font = chosen.Length == 0 ? null : chosen }); ApplyColour(); };
            font.Items.Add(mi);
        }

        var size = new MenuItem { Header = "Text _size" };
        foreach (var (pt, name) in Sizes)
        {
            var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = Math.Abs(Data.TextSize - pt) < 0.5 };
            var chosen = pt;
            mi.Click += (_, _) => { Save(Data with { TextSize = chosen }); ApplyColour(); };
            size.Items.Add(mi);
        }
        return [textColour, font, size];
    }

    protected override void ApplyColour()
    {
        base.ApplyColour();
        var c = Colours;
        var ink = Controls.ColorPicker.Parse(Data.TextColour) is { } chosen ? new SolidColorBrush(chosen) : InkBrush;
        _box.Foreground = _placeholder.Foreground = ink;
        _box.CaretBrush = ink;
        _box.SelectionBrush = new SolidColorBrush(c.Accent);
        if (Data.Font is { Length: > 0 } family)
        {
            _box.FontFamily = _placeholder.FontFamily = new FontFamily(family);
        }
        else
        {
            _box.SetResourceReference(FontFamilyProperty, "Font.Sans");
            _placeholder.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Sans");
        }
        _box.FontSize = _placeholder.FontSize = Data.TextSize is > 6 and < 60 ? Data.TextSize : 15;
        _tape.Fill = new SolidColorBrush(Color.FromArgb(0xB0, c.Accent.R, c.Accent.G, c.Accent.B));
        _sparkle.Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    }
}

/// <summary>The time, big, with the date under it, on a card with little cat ears.</summary>
public sealed class ClockWidget : WidgetWindow
{
    private readonly TextBlock _time, _ampm, _date;
    private readonly Path _ears;
    private readonly DispatcherTimer _tick;
    private string _shown = "";

    public ClockWidget(DesktopWidget data, DesktopWidgetManager manager) : base(data, manager, 250, 128)
    {
        Card.Margin = new Thickness(14, 26, 14, 14);
        _time = Text("", 48, FontWeights.Black, Brushes.Black);
        _ampm = Text("", 16, FontWeights.Bold, Brushes.Black);
        _ampm.Margin = new Thickness(6, 0, 0, 10);
        _ampm.VerticalAlignment = VerticalAlignment.Bottom;
        _date = Text("", 14, FontWeights.SemiBold, Brushes.Black);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { _time, _ampm } };
        _date.HorizontalAlignment = HorizontalAlignment.Center;
        _date.Margin = new Thickness(0, -4, 0, 0);
        Card.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), Children = { row, _date } };
        // cat ears poking up over the card's top corners
        _ears = new Path
        {
            Data = Geometry.Parse("M38,30 L48,4 L70,28 Z M240,30 L230,4 L208,28 Z"), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        DetachBody();
        var root = new Grid();
        root.Children.Add(_ears);
        root.Children.Add(Card);
        UseBody(root, 250 + 28, 128 + 28 + 12);
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _tick.Start();
        Closed += (_, _) => _tick.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var key = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + Data.Clock24;
        if (key == _shown) return;
        _shown = key;
        var culture = CultureInfo.CurrentCulture;
        _time.Text = Data.Clock24 ? now.ToString("HH:mm", culture) : now.ToString("h:mm", culture);
        _ampm.Text = Data.Clock24 ? "" : now.ToString("tt", culture);
        _date.Text = now.ToString("dddd d MMMM", culture);
    }

    protected override IEnumerable<object> MenuItems()
    {
        var h24 = new MenuItem { Header = "_24-hour time", IsCheckable = true, IsChecked = Data.Clock24 };
        h24.Click += (_, _) => { Save(Data with { Clock24 = !Data.Clock24 }); _shown = ""; Refresh(); ApplyColour(); };
        return [h24];
    }

    protected override void ApplyColour()
    {
        base.ApplyColour();
        var c = Colours;
        _time.Foreground = _date.Foreground = InkBrush;
        _ampm.Foreground = new SolidColorBrush(c.Accent);
        _ears.Fill = new LinearGradientBrush(c.Top, c.Bottom, 90);
        _ears.Stroke = new SolidColorBrush(Color.FromArgb(0x99, c.Accent.R, c.Accent.G, c.Accent.B));
    }
}

/// <summary>Days to go until something you're looking forward to. Double-click (or right-click → Edit) to change it.</summary>
public sealed class CountdownWidget : WidgetWindow
{
    private readonly TextBlock _title, _days, _unit, _detail;
    private readonly StackPanel _view, _edit;
    private readonly TextBox _titleBox;
    private readonly DatePicker _datePicker;
    private readonly Path _sparkle;
    private readonly DispatcherTimer _tick;

    public CountdownWidget(DesktopWidget data, DesktopWidgetManager manager) : base(data, manager, 230, 176)
    {
        _title = Text("", 15, FontWeights.Bold, Brushes.Black);
        _days = Text("", 54, FontWeights.Black, Brushes.Black);
        _unit = Text("", 15, FontWeights.Bold, Brushes.Black);
        _unit.Margin = new Thickness(8, 0, 0, 14);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _detail = Text("", 13, FontWeights.SemiBold, Brushes.Black);
        _detail.Opacity = 0.75;
        _view = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 16), VerticalAlignment = VerticalAlignment.Center,
            Children = { _title, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -6, 0, -6), Children = { _days, _unit } }, _detail },
        };

        _titleBox = new TextBox { FontSize = 14, Margin = new Thickness(0, 4, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(_titleBox, "What you're counting down to");
        _datePicker = new DatePicker { FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(_datePicker, "The date");
        var done = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 4, 14, 4) };
        done.SetResourceReference(StyleProperty, "Btn.Primary");
        done.Click += (_, _) => FinishEditing();
        _edit = new StackPanel
        {
            Margin = new Thickness(18, 12, 18, 12), Visibility = Visibility.Collapsed,
            Children = { new TextBlock { Text = "Counting down to", FontSize = 12, Opacity = 0.75 }, _titleBox, _datePicker, done },
        };
        _sparkle = Sparkle(7, Brushes.White);
        _sparkle.HorizontalAlignment = HorizontalAlignment.Right;
        _sparkle.VerticalAlignment = VerticalAlignment.Top;
        _sparkle.Margin = new Thickness(0, 14, 18, 0);
        Card.Child = new Grid { Children = { _sparkle, _view, _edit } };
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _tick.Start();
        Closed += (_, _) => _tick.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var target = Data.Target ?? DesktopWidget.NextChristmas(DateTime.Now);
        var (days, hours, minutes, arrived) = DesktopWidget.Remaining(target, DateTime.Now);
        _title.Text = string.IsNullOrWhiteSpace(Data.Text) ? "Countdown" : Data.Text;
        if (arrived)
        {
            _days.Text = "It's here!";
            _days.FontSize = 34;
            _unit.Text = "";
            _detail.Text = target.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
            return;
        }
        _days.FontSize = 54;
        _days.Text = days.ToString(CultureInfo.CurrentCulture);
        _unit.Text = days == 1 ? "day to go" : "days to go";
        _detail.Text = $"and {hours} h {minutes} min · {target.ToString("d MMM", CultureInfo.CurrentCulture)}";
    }

    protected override void OnDoubleClick() => StartEditing();

    private void StartEditing()
    {
        _titleBox.Text = Data.Text;
        _datePicker.SelectedDate = (Data.Target ?? DesktopWidget.NextChristmas(DateTime.Now)).Date;
        _view.Visibility = Visibility.Collapsed;
        _edit.Visibility = Visibility.Visible;
        Activate();
        _titleBox.Focus();
    }

    private void FinishEditing()
    {
        var date = _datePicker.SelectedDate ?? Data.Target ?? DesktopWidget.NextChristmas(DateTime.Now);
        Save(Data with { Text = _titleBox.Text.Trim(), Target = date.Date });
        _edit.Visibility = Visibility.Collapsed;
        _view.Visibility = Visibility.Visible;
        Refresh();
    }

    protected override IEnumerable<object> MenuItems()
    {
        var edit = new MenuItem { Header = "_Edit countdown…" };
        edit.Click += (_, _) => StartEditing();
        return [edit];
    }

    protected override void ApplyColour()
    {
        base.ApplyColour();
        var c = Colours;
        _title.Foreground = _days.Foreground = _detail.Foreground = _titleBox.Foreground = InkBrush;
        _unit.Foreground = new SolidColorBrush(c.Accent);
        _sparkle.Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        foreach (var t in _edit.Children.OfType<TextBlock>()) t.Foreground = InkBrush;
    }
}

/// <summary>
/// Today's weather for a place you type in: a little picture (sun, clouds, rain, snow, storm, fog or a
/// moon at night, with a face), the temperature, the day's high and low. From Open-Meteo (free, no
/// account): only the place name and its coordinates are sent. Refreshes every 30 minutes.
/// </summary>
public sealed class WeatherWidget : WidgetWindow
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly TextBlock _temp, _words, _place, _range;
    private readonly Canvas _picture = new() { Width = 64, Height = 64 };
    private readonly StackPanel _view, _edit;
    private readonly TextBox _cityBox;
    private readonly CheckBox _fahrenheit;
    private readonly TextBlock _editNote;
    private readonly DispatcherTimer _tick;
    private (double temp, int code, bool day, double hi, double lo)? _last;

    static WeatherWidget() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("AmperDock/1.0");

    public WeatherWidget(DesktopWidget data, DesktopWidgetManager manager) : base(data, manager, 250, 150)
    {
        _temp = Text("", 44, FontWeights.Black, Brushes.Black);
        _words = Text("", 14, FontWeights.Bold, Brushes.Black);
        _place = Text("", 12, FontWeights.SemiBold, Brushes.Black);
        _place.Opacity = 0.75;
        _range = Text("", 12, FontWeights.SemiBold, Brushes.Black);
        _range.Opacity = 0.75;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Children = { _temp, _words, _range } };
        _view = new StackPanel
        {
            Margin = new Thickness(18, 12, 18, 12), VerticalAlignment = VerticalAlignment.Center,
            Children = { _place, new StackPanel { Orientation = Orientation.Horizontal, Children = { new Viewbox { Width = 72, Height = 72, Child = _picture }, words } } },
        };
        _cityBox = new TextBox { FontSize = 14, Margin = new Thickness(0, 4, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(_cityBox, "Your town or city");
        _cityBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) FinishEditing(); };
        _fahrenheit = new CheckBox { Content = "Show °F", Margin = new Thickness(0, 0, 0, 8) };
        _editNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 6) };
        var done = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 4, 14, 4) };
        done.SetResourceReference(StyleProperty, "Btn.Primary");
        done.Click += (_, _) => FinishEditing();
        _edit = new StackPanel
        {
            Margin = new Thickness(18, 10, 18, 10), Visibility = Visibility.Collapsed,
            Children = { new TextBlock { Text = "Weather for", FontSize = 12, Opacity = 0.75 }, _cityBox, _fahrenheit, _editNote, done },
        };
        Card.Child = new Grid { Children = { _view, _edit } };
        _tick = new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background, async (_, _) => await Fetch(), Dispatcher);
        _tick.Start();
        Closed += (_, _) => _tick.Stop();
        Loaded += async (_, _) =>
        {
            if (Data.Latitude is null) StartEditing();
            else await Fetch();
        };
        Show(null);
    }

    protected override void OnDoubleClick() => StartEditing();

    protected override IEnumerable<object> MenuItems()
    {
        var place = new MenuItem { Header = "Change _place…" };
        place.Click += (_, _) => StartEditing();
        var refresh = new MenuItem { Header = "_Refresh" };
        refresh.Click += async (_, _) => await Fetch();
        var f = new MenuItem { Header = "Show _°F", IsCheckable = true, IsChecked = Data.Fahrenheit };
        f.Click += async (_, _) => { Save(Data with { Fahrenheit = !Data.Fahrenheit }); await Fetch(); };
        return [place, refresh, f];
    }

    private void StartEditing()
    {
        _cityBox.Text = Data.Place?.Split(',')[0] ?? "";
        _fahrenheit.IsChecked = Data.Fahrenheit;
        _editNote.Text = Data.Latitude is null ? "Type your town or city, then Done." : "";
        _view.Visibility = Visibility.Collapsed;
        _edit.Visibility = Visibility.Visible;
        Activate();
        _cityBox.Focus();
    }

    private async void FinishEditing()
    {
        var city = _cityBox.Text.Trim();
        if (city.Length == 0) { _editNote.Text = "Type a town or city first."; return; }
        _editNote.Text = "Looking it up…";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync(
                "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name=" + Uri.EscapeDataString(city)));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                _editNote.Text = $"Couldn't find \"{city}\". Check the spelling, or try a bigger town nearby.";
                return;
            }
            var r = results[0];
            var name = r.GetProperty("name").GetString() ?? city;
            var country = r.TryGetProperty("country", out var c) ? c.GetString() : null;
            Save(Data with
            {
                Place = country is null ? name : $"{name}, {country}",
                Latitude = r.GetProperty("latitude").GetDouble(), Longitude = r.GetProperty("longitude").GetDouble(),
                Fahrenheit = _fahrenheit.IsChecked == true,
            });
            _edit.Visibility = Visibility.Collapsed;
            _view.Visibility = Visibility.Visible;
            await Fetch();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _editNote.Text = "Can't reach the weather service. Check your internet connection and try again.";
        }
    }

    private async Task Fetch()
    {
        if (Data.Latitude is not { } lat || Data.Longitude is not { } lon) return;
        var inv = CultureInfo.InvariantCulture;
        var url = "https://api.open-meteo.com/v1/forecast?current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min"
                  + $"&forecast_days=1&timezone=auto&latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}"
                  + (Data.Fahrenheit ? "&temperature_unit=fahrenheit" : "");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            _last = (cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32(), cur.GetProperty("is_day").GetInt32() == 1,
                daily.GetProperty("temperature_2m_max")[0].GetDouble(), daily.GetProperty("temperature_2m_min")[0].GetDouble());
            Show(null);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Show("Can't reach the weather right now");
        }
    }

    private void Show(string? problem)
    {
        _place.Text = Data.Place ?? "Weather";
        if (_last is not { } w)
        {
            _temp.Text = "–°";
            _words.Text = problem ?? (Data.Latitude is null ? "Where are you?" : "Fetching…");
            _range.Text = "";
            Draw("cloud", day: true);
            return;
        }
        var (words, picture) = DesktopWidget.WeatherCode(w.code);
        _temp.Text = $"{Math.Round(w.temp)}°";
        _words.Text = problem ?? words;
        _range.Text = $"H {Math.Round(w.hi)}°  L {Math.Round(w.lo)}°";
        Draw(picture, w.day);
    }

    /// <summary>The weather picture on a 64-unit canvas: soft shapes with ink outlines, and a face on the sun or cloud.</summary>
    private void Draw(string picture, bool day)
    {
        _picture.Children.Clear();
        var ink = new SolidColorBrush(Color.FromRgb(0x2A, 0x1F, 0x3D));
        Path P(string g, Brush? fill, double stroke = 2)
        {
            var path = new Path { Data = Geometry.Parse(g), Fill = fill, Stroke = ink, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _picture.Children.Add(path);
            return path;
        }
        void Face(double cx, double cy, double k = 1)
        {
            P($"M{cx - 5 * k},{cy} a1.3,1.3 0 1 0 0.01,0 M{cx + 5 * k},{cy} a1.3,1.3 0 1 0 0.01,0", ink, 1.2);
            P($"M{cx - 1.6 * k},{cy + 2.6 * k} q{1.6 * k},{1.6 * k} {3.2 * k},0", null, 1.3);
            P($"M{cx - 9 * k},{cy + 2.4 * k} a2.2,1.3 0 1 0 0.01,0 M{cx + 9 * k},{cy + 2.4 * k} a2.2,1.3 0 1 0 0.01,0", new SolidColorBrush(Color.FromArgb(0xCC, 0xF4, 0xA6, 0xC8)), 0);
        }
        var sunFill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x5C));
        var cloudFill = new SolidColorBrush(Colors.White);
        const string cloud = "M14,46 a8,8 0 0 1 2,-15.6 a12,12 0 0 1 22.6,-4 a10,10 0 0 1 15.4,9.6 a6,6 0 0 1 -0.6,10 Z";
        switch (picture)
        {
            case "sun" when !day:
            case "partly" when !day:
                P("M40,10 c-12,0 -22,9.6 -22,22 s10,22 22,22 c5,0 9.6,-1.6 13.4,-4.6 c-1.8,0.4 -3.6,0.6 -5.4,0.6 c-11,0 -19.6,-8.6 -19.6,-19 s8.6,-19.4 19.6,-19.4 c1.8,0 3.6,0.2 5.4,0.6 c-3.8,-2.4 -8.4,-2.2 -13.4,-2.2 Z", new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xA8)));
                Face(27, 34, 0.7);
                break;
            case "sun":
                for (var i = 0; i < 8; i++)
                {
                    var a = i * Math.PI / 4;
                    P($"M{32 + Math.Cos(a) * 21},{32 + Math.Sin(a) * 21} L{32 + Math.Cos(a) * 28},{32 + Math.Sin(a) * 28}", null, 3.4).Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4D));
                }
                P("M15,32 a17,17 0 1 0 34,0 a17,17 0 1 0 -34,0 Z", sunFill);
                Face(32, 32);
                break;
            case "partly":
                P("M24,22 a14,14 0 1 0 28,0 a14,14 0 1 0 -28,0 Z", sunFill);
                P(cloud, cloudFill);
                Face(30, 39, 0.85);
                break;
            default:
                P(cloud, picture == "storm" ? new SolidColorBrush(Color.FromRgb(0xC9, 0xC4, 0xD3)) : cloudFill);
                Face(32, 37, 0.85);
                if (picture == "rain")
                    foreach (var x in new[] { 20.0, 31, 42 }) P($"M{x},51 q-2.4,4 0,6 q2.4,-2 0,-6 Z", new SolidColorBrush(Color.FromRgb(0x8C, 0xCB, 0xFF)), 1.2);
                if (picture == "snow")
                    foreach (var x in new[] { 20.0, 32, 44 }) P($"M{x},50 v8 M{x - 3.4},52 l6.8,4 M{x - 3.4},56 l6.8,-4", null, 1.4).Stroke = new SolidColorBrush(Color.FromRgb(0x8C, 0xCB, 0xFF));
                if (picture == "storm") P("M32,47 l-5,8 h5 l-3,8 l9,-11 h-5 l3,-5 Z", sunFill, 1.4);
                if (picture == "fog") foreach (var y in new[] { 51.0, 56 }) P($"M14,{y} h36", null, 2.4).Stroke = new SolidColorBrush(Color.FromRgb(0xB9, 0xA4, 0xFF));
                break;
        }
    }

    protected override void ApplyColour()
    {
        base.ApplyColour();
        var c = Colours;
        _temp.Foreground = _place.Foreground = _range.Foreground = _cityBox.Foreground = _editNote.Foreground = InkBrush;
        _words.Foreground = new SolidColorBrush(c.Accent);
        _fahrenheit.Foreground = InkBrush;
        foreach (var t in _edit.Children.OfType<TextBlock>()) t.Foreground = InkBrush;
    }
}
