using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmperDock.App.Controls;
using AmperDock.App.Dock;
using AmperDock.Core;

namespace AmperDock.App.Settings;

/// <summary>
/// Dock appearance: style, three colour slots edited with a full picker, gradient direction, corners,
/// opacity and outline. Live preview on the dock; Cancel restores what was there.
/// </summary>
public partial class AppearanceWindow : Window
{
    private enum Slot { Fill, GradientEnd, Outline, Indicator }

    /// <summary>Starter palette, chosen to sit well with the lilac/plum brand.</summary>
    private static readonly string[] PaletteHex =
    [
        "#F6F0FF", "#D9C8FF", "#B497FF", "#7444D6", "#F9D3E3", "#F4A6C8", "#E86FA6", "#FFDCC8",
        "#FFF1C9", "#D5F2E3", "#7ED9AE", "#D6E9FF", "#9CC9F5", "#FFF6E8", "#2A1F3D", "#18121F",
    ];

    private static readonly (string id, string name, string blurb)[] Styles =
    [
        (DockStyles.Glass, "Glass", "Tinted, with a soft shine"),
        (DockStyles.Solid, "Solid", "One flat colour"),
        (DockStyles.Gradient, "Gradient", "Blend two colours"),
        (DockStyles.Outline, "Outline", "Just a line"),
    ];

    private const string RecentKey = "recent_colors";

    private readonly DockHost _dock;
    private readonly SettingsStore _store;
    private readonly DockAppearance _original;
    private DockAppearance _current;
    private Slot _slot = Slot.Fill;
    private bool _loading = true, _saved;

    public AppearanceWindow(DockHost dock, SettingsStore store)
    {
        _dock = dock;
        _store = store;
        _original = _current = dock.Appearance;
        InitializeComponent();

        BuildStyleCards();
        BuildThemeCards();
        BuildIndicatorCards();
        foreach (var hex in PaletteHex) PaletteSwatches.Children.Add(ColorPicker.Swatch(hex, () => SetSlot(_slot, hex)));
        Picker.SetRecent(LoadRecent());
        Picker.ColorChanged += c => { if (!_loading) SetSlot(_slot, ColorPicker.Hex(c)); };
        Picker.LostKeyboardFocus += (_, _) => RememberCurrent();
        Picker.PreviewMouseLeftButtonUp += (_, _) => RememberCurrent();

        LoadControls(_current);
        _loading = false;
        Closed += (_, _) => { if (!_saved) _dock.PreviewAppearance(_original); };
    }

    // ---------------------------------------------------------------- state <-> controls

    private void LoadControls(DockAppearance a)
    {
        _loading = true;
        Roundness.Value = a.Roundness * 100;
        OpacitySlider.Value = a.Opacity;
        Outline.IsChecked = a.Outline;
        OutlineWidth.Value = a.OutlineWidth;
        Angle.Value = a.GradientAngle;
        DecorationsBox.IsChecked = a.Decorations;
        _current = a;
        SyncAll();
        _loading = false;
    }

    private DockAppearance ReadSliders() => _current with
    {
        Roundness = (float)(Roundness.Value / 100),
        Opacity = (int)Math.Round(OpacitySlider.Value),
        Outline = Outline.IsChecked == true,
        OutlineWidth = (float)OutlineWidth.Value,
        GradientAngle = (float)Angle.Value,
        Decorations = DecorationsBox.IsChecked == true,
    };

    private void Apply(DockAppearance a)
    {
        _current = a.Normalised();
        _dock.PreviewAppearance(_current);
        SyncAll();
    }

    /// <summary>Refresh everything that depends on the current appearance (cards, slots, picker, labels).</summary>
    private void SyncAll()
    {
        var a = _current;
        OpacityValue.Text = a.Opacity == 0 ? "Invisible" : $"{a.Opacity}%";
        GradientPanel.Visibility = a.Style == DockStyles.Gradient ? Visibility.Visible : Visibility.Collapsed;
        OutlineWidthRow.IsEnabled = a.Outline || a.Style == DockStyles.Outline;
        if (_slot == Slot.GradientEnd && a.Style != DockStyles.Gradient) _slot = Slot.Fill;
        if (_slot == Slot.Fill && a.Style == DockStyles.Outline) _slot = Slot.Outline;

        foreach (Border card in StyleCards.Children) Select(card, (string)card.Tag == a.Style);
        foreach (var card in ThemeCards.Children.OfType<Border>()) Select(card, (string)card.Tag == a.Theme);
        foreach (Border card in IndicatorCards.Children) Select(card, (string)card.Tag == a.Indicator);
        foreach (RadioButton rb in BandShapes.Children) rb.IsChecked = (string)rb.Tag == a.Shape;
        CornersPanel.Visibility = a.Shape == DockShapes.Rounded ? Visibility.Visible : Visibility.Collapsed;
        var theme = DockThemeCatalog.Shared.Find(a.Theme);
        DecorationsBox.Visibility = theme is { } t && (t.Ears || t.Snow || t.Decor.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        if (_slot == Slot.Indicator && !DockIndicators.Drawn.Contains(a.Indicator)) _slot = Slot.Fill;
        foreach (RadioButton rb in ShapePresets.Children)
            rb.IsChecked = MathF.Abs(float.Parse((string)rb.Tag, CultureInfo.InvariantCulture) - a.Roundness) < 0.005f;
        BuildSlots();
        SyncPicker();
    }

    private void SyncPicker()
    {
        var hex = SlotValue(_slot);
        PickerTitle.Text = SlotName(_slot);
        PickerHint.Text = hex is null ? "Following the theme. Pick any colour to customise." : $"Custom: {hex}";
        UseTheme.Visibility = hex is null ? Visibility.Collapsed : Visibility.Visible;
        var wasLoading = _loading;
        _loading = true;
        Picker.Color = ColorPicker.Parse(hex ?? ThemeColourFor(_slot)) ?? Colors.White;
        _loading = wasLoading;
    }

    // ---------------------------------------------------------------- colour slots

    private string? SlotValue(Slot s) => s switch
    {
        Slot.Fill => _current.Color,
        Slot.GradientEnd => _current.Color2,
        Slot.Indicator => _current.IndicatorColor,
        _ => _current.OutlineColor,
    };

    private static string SlotName(Slot s) => s switch
    {
        Slot.Fill => "Fill colour",
        Slot.GradientEnd => "Gradient end colour",
        Slot.Indicator => "Indicator colour",
        _ => "Outline colour",
    };

    /// <summary>What the theme would use for a slot, so the picker starts from what you see.</summary>
    private string ThemeColourFor(Slot s)
    {
        var dark = Application.Current.Resources["Color.Bg"] is Color bg && bg.R < 128;
        return s switch
        {
            Slot.Outline => dark ? "#3C3052" : "#E2D6F5",
            Slot.Indicator => dark ? "#B497FF" : "#7444D6",
            Slot.GradientEnd => _current.Color ?? (dark ? "#3A2C52" : "#FFFFFF"),
            _ => dark ? "#221A2E" : "#F6F0FF",
        };
    }

    private void SetSlot(Slot s, string? hex)
    {
        if (s == Slot.Outline && hex is not null && Outline.IsChecked != true)
        {
            _loading = true; Outline.IsChecked = true; _loading = false;   // picking an outline colour turns the outline on
        }
        var next = s switch
        {
            Slot.Fill => ReadSliders() with { Color = hex },
            Slot.GradientEnd => ReadSliders() with { Color2 = hex },
            Slot.Indicator => ReadSliders() with { IndicatorColor = hex },
            _ => ReadSliders() with { OutlineColor = hex },
        };
        Apply(next);
    }

    private void BuildSlots()
    {
        Slots.Children.Clear();
        var slots = new List<Slot>();
        if (_current.Style != DockStyles.Outline) slots.Add(Slot.Fill);
        if (_current.Style == DockStyles.Gradient) slots.Add(Slot.GradientEnd);
        slots.Add(Slot.Outline);
        if (DockIndicators.Drawn.Contains(_current.Indicator)) slots.Add(Slot.Indicator);

        foreach (var s in slots)
        {
            var hex = SlotValue(s);
            var selected = s == _slot;
            var swatch = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Background = hex is null ? ThemeSwatchBrush() : new SolidColorBrush(ColorPicker.Parse(hex)!.Value),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            var name = new TextBlock { Text = SlotName(s), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            name.SetResourceReference(StyleProperty, "Text.BodyStrong");
            var value = new TextBlock { Text = hex ?? "Theme", VerticalAlignment = VerticalAlignment.Center };
            value.SetResourceReference(StyleProperty, "Text.Muted");
            DockPanel.SetDock(swatch, System.Windows.Controls.Dock.Left);
            DockPanel.SetDock(name, System.Windows.Controls.Dock.Left);
            DockPanel.SetDock(value, System.Windows.Controls.Dock.Right);

            var row = new Border
            {
                Padding = new Thickness(10, 8, 12, 8), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(selected ? 2 : 1), Focusable = true, Cursor = Cursors.Hand,
                Child = new DockPanel { LastChildFill = false, Children = { swatch, name, value } },
            };
            row.SetResourceReference(Border.BorderBrushProperty, selected ? "Brush.FocusRing" : "Brush.Border");
            row.SetResourceReference(Border.BackgroundProperty, selected ? "Brush.AccentSoft" : "Brush.SurfaceRaised");
            AutomationProperties.SetName(row, $"{SlotName(s)}, {hex ?? "theme colour"}{(selected ? ", selected" : "")}");
            void Choose() { _slot = s; BuildSlots(); SyncPicker(); }
            row.MouseLeftButtonUp += (_, _) => Choose();
            row.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Choose(); e.Handled = true; } };
            Slots.Children.Add(row);
        }
    }

    private static Brush ThemeSwatchBrush() => new LinearGradientBrush
    {
        StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop((Color)ColorConverter.ConvertFromString("#F6F0FF"), 0.5),
            new GradientStop((Color)ColorConverter.ConvertFromString("#221A2E"), 0.5),
        },
    };

    // ---------------------------------------------------------------- style cards

    private void BuildStyleCards()
    {
        foreach (var (id, name, blurb) in Styles)
        {
            var sample = new Border { Height = 22, Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(11) };
            var lilac = Color.FromRgb(0xB4, 0x97, 0xFF);
            switch (id)
            {
                case DockStyles.Glass:
                    sample.Background = new LinearGradientBrush(Color.FromArgb(230, 255, 255, 255), Color.FromArgb(150, 217, 200, 255), 90);
                    sample.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xB6, 0xF5));
                    sample.BorderThickness = new Thickness(1);
                    break;
                case DockStyles.Solid:
                    sample.Background = new SolidColorBrush(lilac);
                    break;
                case DockStyles.Gradient:
                    sample.Background = new LinearGradientBrush(lilac, Color.FromRgb(0xF4, 0xA6, 0xC8), 0);
                    break;
                default:
                    sample.BorderBrush = new SolidColorBrush(lilac);
                    sample.BorderThickness = new Thickness(2);
                    break;
            }
            var title = new TextBlock { Text = name };
            title.SetResourceReference(StyleProperty, "Text.BodyStrong");
            var sub = new TextBlock { Text = blurb };
            sub.SetResourceReference(StyleProperty, "Text.Caption");
            var card = new Border
            {
                Tag = id, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(12),
                Focusable = true, Cursor = Cursors.Hand, Child = new StackPanel { Children = { sample, title, sub } },
            };
            AutomationProperties.SetName(card, $"{name} style: {blurb}");
            void Choose() => Apply(ReadSliders() with { Style = id });
            card.MouseLeftButtonUp += (_, _) => Choose();
            card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Choose(); e.Handled = true; } };
            StyleCards.Children.Add(card);
        }
    }

    // ---------------------------------------------------------------- themes and indicators

    private void BuildThemeCards()
    {
        foreach (var t in DockThemeCatalog.Shared.Themes)
        {
            var pic = new Image { Source = DockThemeCatalog.Image(t.Preview), Width = 156, Height = 68, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
            var title = new TextBlock { Text = t.Name, Margin = new Thickness(0, 4, 0, 0) };
            title.SetResourceReference(StyleProperty, "Text.BodyStrong");
            var panel = new StackPanel { Children = { pic, title } };
            if (t.Season is { } season)
            {
                var chip = new TextBlock { Text = season };
                chip.SetResourceReference(StyleProperty, "Text.Caption");
                panel.Children.Add(chip);
            }
            var card = new Border
            {
                Tag = t.Id, Width = 176, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 8, 10, 10),
                CornerRadius = new CornerRadius(12), Focusable = true, Cursor = Cursors.Hand, Child = panel, ToolTip = t.Blurb,
            };
            AutomationProperties.SetName(card, $"{t.Name} theme{(t.Season is null ? "" : $", {t.Season}")}: {t.Blurb}");
            void Choose()
            {
                _slot = Slot.Fill;
                var look = t.Appearance;
                LoadControls(look);
                Apply(look);
            }
            card.MouseLeftButtonUp += (_, _) => Choose();
            card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Choose(); e.Handled = true; } };
            ThemeCards.Children.Add(card);
        }
        if (ThemeCards.Children.Count == 0)
        {
            var none = new TextBlock { Text = "Themes aren't installed with this copy of AmperDock." };
            none.SetResourceReference(StyleProperty, "Text.Muted");
            ThemeCards.Children.Add(none);
        }
    }

    private void BuildIndicatorCards()
    {
        var names = new Dictionary<string, string>
        {
            ["paw"] = "Paw", ["dot"] = "Dot", ["mushroom"] = "Mushroom", ["star"] = "Star",
            ["leaf"] = "Leaf", ["pumpkin"] = "Pumpkin", ["petal"] = "Petal", ["heart"] = "Heart",
        };
        foreach (var (id, name) in names)
        {
            FrameworkElement glyph;
            if (id == "dot")
                glyph = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(0x74, 0x44, 0xD6)) };
            else if (id == "paw")
                glyph = PawGlyph();
            else if (DockThemeCatalog.Shared.Indicators.TryGetValue(id, out var png))
                glyph = new Image { Source = DockThemeCatalog.Image(png), Width = 22, Height = 22 };
            else continue;
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            var label = new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            label.SetResourceReference(StyleProperty, "Text.Caption");
            var card = new Border
            {
                Tag = id, Width = 72, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(6, 8, 6, 6), CornerRadius = new CornerRadius(10),
                Focusable = true, Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { new Grid { Height = 24, Children = { glyph } }, label } },
            };
            AutomationProperties.SetName(card, $"{name} indicator");
            void Choose() => Apply(ReadSliders() with { Indicator = id });
            card.MouseLeftButtonUp += (_, _) => Choose();
            card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Choose(); e.Handled = true; } };
            IndicatorCards.Children.Add(card);
        }
    }

    /// <summary>The paw indicator's shape (the same geometry the dock draws), for the indicator cards.</summary>
    private static FrameworkElement PawGlyph()
    {
        var c = new Canvas { Width = 24, Height = 24 };
        var brush = new SolidColorBrush(Color.FromRgb(0x74, 0x44, 0xD6));
        foreach (var (x, y, rx, ry, deg) in new[] { (12.0, 16.2, 5.6, 4.6, 0.0), (5.2, 10.4, 2.2, 2.7, -18.0), (9.4, 6.2, 2.3, 2.9, -6.0), (14.6, 6.2, 2.3, 2.9, 6.0), (18.8, 10.4, 2.2, 2.7, 18.0) })
        {
            var e = new System.Windows.Shapes.Ellipse
            {
                Width = rx * 2, Height = ry * 2, Fill = brush, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(deg),
            };
            Canvas.SetLeft(e, x - rx);
            Canvas.SetTop(e, y - ry);
            c.Children.Add(e);
        }
        return new Viewbox { Width = 20, Height = 20, Child = c };
    }

    private void OnBandShape(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(ReadSliders() with { Shape = (string)((RadioButton)sender).Tag });
    }

    private static void Select(Border card, bool on)
    {
        card.BorderThickness = new Thickness(on ? 2 : 1);
        card.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.FocusRing" : "Brush.Border");
        card.SetResourceReference(Border.BackgroundProperty, on ? "Brush.AccentSoft" : "Brush.SurfaceRaised");
    }

    // ---------------------------------------------------------------- recent colours

    private IEnumerable<string> LoadRecent()
    {
        try { return JsonSerializer.Deserialize<string[]>(_store.GetSetting(RecentKey) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    private void RememberCurrent()
    {
        if (SlotValue(_slot) is null) return;
        Picker.PushRecent();
        _store.SetSetting(RecentKey, JsonSerializer.Serialize(Picker.Recent));
    }

    // ---------------------------------------------------------------- events

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(ReadSliders());
    }

    private void OnShapePreset(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Roundness.Value = float.Parse((string)((RadioButton)sender).Tag, CultureInfo.InvariantCulture) * 100;
    }

    private void OnAnglePreset(object sender, RoutedEventArgs e) =>
        Angle.Value = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);

    private void OnUseTheme(object sender, RoutedEventArgs e) => SetSlot(_slot, null);

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _slot = Slot.Fill;
        LoadControls(DockAppearance.Default);
        Apply(DockAppearance.Default);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();   // Closed restores the original

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _dock.SaveAppearance(ReadSliders());
        _saved = true;
        Close();
    }
}
