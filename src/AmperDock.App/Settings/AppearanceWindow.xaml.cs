using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AmperDock.App.Dock;
using AmperDock.Core;

namespace AmperDock.App.Settings;

/// <summary>Dock appearance: corners, colour, opacity. Live preview on the dock; Cancel restores what was there.</summary>
public partial class AppearanceWindow : Window
{
    /// <summary>Starter palette, chosen to sit well with the lilac/plum brand. null = follow the theme.</summary>
    private static readonly (string name, string? hex)[] Palette =
    [
        ("Theme (follows light/dark)", null),
        ("Lilac", "#F6F0FF"), ("Lavender", "#D9C8FF"), ("Blush", "#F9D3E3"), ("Peach", "#FFDCC8"),
        ("Butter", "#FFF1C9"), ("Mint", "#D5F2E3"), ("Sky", "#D6E9FF"), ("Cream", "#FFF6E8"),
        ("Plum", "#2A1F3D"), ("Midnight", "#18121F"), ("Graphite", "#2B2B2E"),
    ];

    private readonly DockHost _dock;
    private readonly DockAppearance _original;
    private DockAppearance _current;
    private bool _loading = true, _saved;

    public AppearanceWindow(DockHost dock)
    {
        _dock = dock;
        _original = _current = dock.Appearance;
        InitializeComponent();
        BuildSwatches();
        LoadControls(_current);
        _loading = false;
        Closed += (_, _) => { if (!_saved) _dock.PreviewAppearance(_original); };
    }

    private void LoadControls(DockAppearance a)
    {
        _loading = true;
        Roundness.Value = a.Roundness * 100;
        OpacitySlider.Value = a.Opacity;
        Outline.IsChecked = a.Outline;
        Hex.Text = a.Color ?? "";
        SyncPresetsAndLabels(a);
        _loading = false;
    }

    private DockAppearance ReadControls() => _current with
    {
        Roundness = (float)(Roundness.Value / 100),
        Opacity = (int)Math.Round(OpacitySlider.Value),
        Outline = Outline.IsChecked == true,
    };

    private void Apply(DockAppearance a)
    {
        _current = a.Normalised();
        SyncPresetsAndLabels(_current);
        _dock.PreviewAppearance(_current);
    }

    private void SyncPresetsAndLabels(DockAppearance a)
    {
        OpacityValue.Text = a.Opacity == 0 ? "Invisible" : $"{a.Opacity}%";
        foreach (RadioButton rb in ShapePresets.Children)
        {
            var preset = float.Parse((string)rb.Tag, CultureInfo.InvariantCulture);
            rb.IsChecked = MathF.Abs(preset - a.Roundness) < 0.005f;
        }
        foreach (Border sw in Swatches.Children)
        {
            var selected = (string?)sw.Tag == a.Color;
            sw.BorderThickness = new Thickness(selected ? 3 : 1);
            sw.SetResourceReference(Border.BorderBrushProperty, selected ? "Brush.FocusRing" : "Brush.Border");
            AutomationPropertiesHelper.SetSelected(sw, selected);
        }
    }

    // ---------------------------------------------------------------- events

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(ReadControls());
    }

    private void OnShapePreset(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Roundness.Value = float.Parse((string)((RadioButton)sender).Tag, CultureInfo.InvariantCulture) * 100;   // fires OnChanged
    }

    private void SetColour(string? hex)
    {
        HexError.Visibility = Visibility.Collapsed;
        _loading = true;
        Hex.Text = hex ?? "";
        _loading = false;
        Apply(ReadControls() with { Color = hex });
    }

    private void OnHexCommitted(object sender, RoutedEventArgs e)
    {
        var text = Hex.Text.Trim();
        if (text.Length == 6 && !text.StartsWith('#')) text = "#" + text;
        if (text.Length == 0) { SetColour(null); return; }
        if (!DockAppearance.IsHexColor(text)) { HexError.Visibility = Visibility.Visible; return; }
        SetColour(text.ToUpperInvariant());
    }

    private void OnHexKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnHexCommitted(sender, e); e.Handled = true; }
    }

    private void OnMoreColours(object sender, RoutedEventArgs e)
    {
        var start = _current.Color ?? "#F6F0FF";
        var picked = ColourDialog.Pick(new WindowInteropHelper(this).Handle, start);
        if (picked is not null) SetColour(picked);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        LoadControls(DockAppearance.Default);
        _current = DockAppearance.Default;
        Apply(_current);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();   // Closed restores the original

    private void OnSave(object sender, RoutedEventArgs e)
    {
        OnHexCommitted(sender, e);
        if (HexError.Visibility == Visibility.Visible) return;
        _dock.SaveAppearance(ReadControls());
        _saved = true;
        Close();
    }

    // ---------------------------------------------------------------- swatches

    private void BuildSwatches()
    {
        foreach (var (name, hex) in Palette)
        {
            var sw = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 6, 6),
                Tag = hex, Focusable = true, Cursor = Cursors.Hand, ToolTip = name,
            };
            if (hex is null)
            {
                // "theme" swatch: half light, half dark lilac
                sw.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#F6F0FF"),
                    (Color)ColorConverter.ConvertFromString("#221A2E"), new Point(0, 0), new Point(1, 1))
                {
                    GradientStops = { new GradientStop((Color)ColorConverter.ConvertFromString("#F6F0FF"), 0.5), new GradientStop((Color)ColorConverter.ConvertFromString("#221A2E"), 0.5) },
                };
            }
            else sw.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            System.Windows.Automation.AutomationProperties.SetName(sw, name);
            sw.MouseLeftButtonUp += (_, _) => SetColour(hex);
            sw.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { SetColour(hex); e.Handled = true; } };
            sw.GotKeyboardFocus += (_, _) => sw.SetResourceReference(Border.BorderBrushProperty, "Brush.FocusRing");
            sw.LostKeyboardFocus += (_, _) => SyncPresetsAndLabels(_current);
            Swatches.Children.Add(sw);
        }
    }
}

/// <summary>Screen readers: mark the chosen swatch as selected via its name suffix.</summary>
internal static class AutomationPropertiesHelper
{
    public static void SetSelected(Border swatch, bool selected)
    {
        var name = (string)swatch.ToolTip;
        System.Windows.Automation.AutomationProperties.SetName(swatch, selected ? $"{name}, selected" : name);
    }
}

/// <summary>The standard Windows colour dialog (full palette plus custom colours).</summary>
internal static class ColourDialog
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public nint hwndOwner, hInstance;
        public int rgbResult;
        public nint lpCustColors;
        public int Flags;
        public nint lCustData, lpfnHook, lpTemplateName;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    private static extern bool ChooseColor(ref CHOOSECOLOR cc);

    private const int CC_RGBINIT = 0x1, CC_FULLOPEN = 0x2;
    private static readonly int[] Custom = new int[16];

    public static string? Pick(nint owner, string startHex)
    {
        var c = (Color)ColorConverter.ConvertFromString(startHex);
        var custom = GCHandle.Alloc(Custom, GCHandleType.Pinned);
        try
        {
            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(), hwndOwner = owner,
                rgbResult = c.R | (c.G << 8) | (c.B << 16),        // COLORREF is 0x00BBGGRR
                lpCustColors = custom.AddrOfPinnedObject(), Flags = CC_RGBINIT | CC_FULLOPEN,
            };
            if (!ChooseColor(ref cc)) return null;
            int r = cc.rgbResult & 0xFF, g = (cc.rgbResult >> 8) & 0xFF, b = (cc.rgbResult >> 16) & 0xFF;
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        finally { custom.Free(); }
    }
}
