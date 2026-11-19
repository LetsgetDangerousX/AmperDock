using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AmperDock.App.Theme;

namespace AmperDock.App.Gallery;

public partial class GalleryWindow : Window
{
    private static readonly (string key, string name)[] Roles =
    [
        ("Accent", "Accent"), ("AccentSoft", "Accent soft"), ("Blush", "Blush"), ("Text", "Text"), ("TextMuted", "Muted"),
        ("Surface", "Surface"), ("Bg", "Background"), ("Border", "Border"), ("Danger", "Danger"), ("Success", "Success"), ("Warning", "Warning"),
    ];

    private readonly ThemeManager _theme;

    public GalleryWindow(ThemeManager theme)
    {
        _theme = theme;
        InitializeComponent();
        BuildSwatches();
        UpdateToggle();
        _theme.Changed += OnThemeChanged;
        Closed += (_, _) => _theme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged() => UpdateToggle();

    private void UpdateToggle() => ThemeToggle.Content = _theme.IsDark ? "Switch to light" : "Switch to dark";

    private void ToggleTheme(object sender, RoutedEventArgs e) => _theme.Apply(!_theme.IsDark);

    private void ShowMenu(object sender, RoutedEventArgs e)
    {
        SampleMenu.PlacementTarget = (UIElement)sender;
        SampleMenu.IsOpen = true;
    }

    private void BuildSwatches()
    {
        foreach (var (key, name) in Roles)
        {
            var swatch = new Border { Height = 40, CornerRadius = new CornerRadius(10, 10, 0, 0) };
            swatch.SetResourceReference(Border.BackgroundProperty, $"Brush.{key}");
            var label = new TextBlock { Text = name, Margin = new Thickness(10, 6, 10, 8) };
            label.SetResourceReference(StyleProperty, "Text.Caption");
            var card = new Border
            {
                Width = 120, Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Child = new StackPanel { Children = { swatch, label } },
            };
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            card.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
            Swatches.Children.Add(card);
        }
    }
}

/// <summary>HotkeyInput primitive: click, press a combination, see it as chips. Esc cancels, Backspace clears.</summary>
public sealed class HotkeyBox : Border
{
    private readonly WrapPanel _chips = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _recording;

    public string? Hotkey { get; private set; }

    public HotkeyBox()
    {
        Focusable = true;
        MinHeight = 36;
        Padding = new Thickness(10, 4, 10, 4);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Cursor = System.Windows.Input.Cursors.Hand;
        SetResourceReference(BorderBrushProperty, "Brush.BorderInput");
        SetResourceReference(BackgroundProperty, "Brush.SurfaceRaised");
        System.Windows.Automation.AutomationProperties.SetName(this, "Hotkey");
        Child = _chips;
        Show();
        MouseLeftButtonDown += (_, _) => { Focus(); Start(); };
        GotKeyboardFocus += (_, _) => SetResourceReference(BorderBrushProperty, "Brush.FocusRing");
        LostKeyboardFocus += (_, _) => { _recording = false; SetResourceReference(BorderBrushProperty, "Brush.BorderInput"); Show(); };
    }

    private void Start()
    {
        _recording = true;
        SetResourceReference(BackgroundProperty, "Brush.AccentSoft");
        _chips.Children.Clear();
        _chips.Children.Add(Text("Press keys…", muted: true));
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (!_recording)
        {
            if (key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { Start(); e.Handled = true; }
            return;
        }
        e.Handled = true;
        switch (key)
        {
            case System.Windows.Input.Key.Escape: _recording = false; Show(); return;
            case System.Windows.Input.Key.Back: Hotkey = null; _recording = false; Show(); return;
            case System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt
                or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin:
                return;   // wait for the non-modifier key
        }
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (mods == System.Windows.Input.ModifierKeys.None) return;   // a global hotkey needs at least one modifier
        var parts = new List<string>();
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        Hotkey = string.Join("+", parts);
        _recording = false;
        Show();
    }

    private void Show()
    {
        SetResourceReference(BackgroundProperty, "Brush.SurfaceRaised");
        _chips.Children.Clear();
        if (Hotkey is null) { _chips.Children.Add(Text("None", muted: true)); return; }
        foreach (var part in Hotkey.Split('+'))
        {
            var chip = new Border { Child = Text(part, muted: false) };
            chip.SetResourceReference(StyleProperty, "Chip");
            _chips.Children.Add(chip);
        }
    }

    private static TextBlock Text(string s, bool muted)
    {
        var t = new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(StyleProperty, muted ? "Text.Muted" : "Text.BodyStrong");
        return t;
    }
}
