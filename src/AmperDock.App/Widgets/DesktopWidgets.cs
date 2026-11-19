using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using AmperDock.Core;

namespace AmperDock.App;

/// <summary>The desktop widgets you can add: a note, a clock and a countdown.</summary>
public static class DesktopWidgetKinds
{
    public static readonly (string Kind, string Name)[] All = [("note", "Note"), ("clock", "Clock"), ("countdown", "Countdown")];
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

    public static Look Get(string id) => All.TryGetValue(id, out var l) ? l : All["lilac"];
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

    private List<DesktopWidget> Load() => DesktopWidget.ListFromJson(_store.GetSetting(DesktopWidget.SettingsKey));
    private void Save(List<DesktopWidget> list) => _store.SetSetting(DesktopWidget.SettingsKey, DesktopWidget.ListToJson(list));

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
        var w = new DesktopWidget
        {
            Kind = kind,
            X = work.Right - 320 - n * 28, Y = work.Top + 60 + n * 28,
            Colour = kind switch { "note" => "pink", "countdown" => "mint", _ => "lilac" },
            Text = kind == "countdown" ? "Christmas" : "",
            Target = kind == "countdown" ? DesktopWidget.NextChristmas(DateTime.Now) : null,
        };
        list.Add(w);
        Save(list);
        Open(w);
    }

    /// <summary>Has the desktop got a widget of this kind already?</summary>
    public bool Has(string kind) => Load().Any(w => w.Kind == kind);

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
        Width = width + 28;
        Height = height + 28;
        Left = data.X;
        Top = data.Y;
        SetResourceReference(FontFamilyProperty, "Font.Sans");

        Card = new Border
        {
            Margin = new Thickness(14), CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22, Color = Color.FromRgb(0x2A, 0x1F, 0x3D) },
        };
        Content = Card;
        Card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox or System.Windows.Controls.Primitives.TextBoxBase) return;
            if (e.ClickCount == 2) { OnDoubleClick(); return; }
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        LocationChanged += (_, _) => { _saveSoon!.Stop(); _saveSoon.Start(); };
        _saveSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _saveSoon!.Stop();
            Save(Data with { X = Left, Y = Top });
        }, Dispatcher);
        SourceInitialized += (_, _) => SitOnDesktop();
        Loaded += (_, _) => ApplyColour();
    }

    protected virtual void OnDoubleClick() { }

    protected void Save(DesktopWidget changed)
    {
        Data = changed;
        Manager.Update(changed);
    }

    protected WidgetColours.Look Colours => WidgetColours.Get(Data.Colour);
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

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in MenuItems()) menu.Items.Add(item);
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var colour = new MenuItem { Header = "_Colour" };
        foreach (var (id, look) in WidgetColours.All)
        {
            var swatch = new Ellipse { Width = 14, Height = 14, Fill = new LinearGradientBrush(look.Top, look.Bottom, 90), Stroke = new SolidColorBrush(look.Accent), StrokeThickness = 1.5 };
            var mi = new MenuItem { Header = look.Name, Icon = swatch, IsCheckable = true, IsChecked = id == Data.Colour };
            mi.Click += (_, _) => { Save(Data with { Colour = id }); ApplyColour(); };
            colour.Items.Add(mi);
        }
        menu.Items.Add(colour);
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

/// <summary>A sticky note with washi tape across the top. Type straight into it; it saves as you go.</summary>
public sealed class NoteWidget : WidgetWindow
{
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly Rectangle _tape;
    private readonly Path _sparkle;
    private readonly DispatcherTimer _typing;

    public NoteWidget(DesktopWidget data, DesktopWidgetManager manager) : base(data, manager, 230, 230)
    {
        _box = new TextBox
        {
            Text = data.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), FontSize = 15, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16, 30, 16, 16), Padding = new Thickness(0),
        };
        System.Windows.Automation.AutomationProperties.SetName(_box, "Note");
        _placeholder = new TextBlock { Text = "Write a little note…", FontSize = 15, Margin = new Thickness(18, 30, 16, 16), IsHitTestVisible = false, Opacity = 0.5 };
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

    protected override void ApplyColour()
    {
        base.ApplyColour();
        var c = Colours;
        _box.Foreground = _placeholder.Foreground = InkBrush;
        _box.CaretBrush = new SolidColorBrush(c.Accent);
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
        Height += 12;
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
        var root = new Grid();
        Content = null;
        root.Children.Add(_ears);
        root.Children.Add(Card);
        Content = root;
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
