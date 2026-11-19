using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AmperDock.App.Controls;

/// <summary>
/// HSV colour picker: a saturation/brightness square, a hue strip, hex + R/G/B entry and a row of
/// recent colours. Mouse: click or drag. Keyboard: Tab to the square (arrows = saturation/brightness,
/// Shift for bigger steps) or the hue strip (Left/Right). Raises ColorChanged as the colour moves.
/// </summary>
public sealed class ColorPicker : StackPanel
{
    private const double SquareW = 248, SquareH = 156, HueH = 16;

    private double _h, _s = 1, _v = 1;   // hue 0..360, saturation and value 0..1
    private bool _updating;

    private readonly Border _square, _hueBar;
    private readonly Rectangle _hueFill;
    private readonly Ellipse _svThumb;
    private readonly Border _hueThumb;
    private readonly Canvas _svCanvas, _hueCanvas;
    private readonly TextBox _hex, _r, _g, _b;
    private readonly Border _swatchNew;
    private readonly WrapPanel _recent;

    public event Action<Color>? ColorChanged;

    public Color Color
    {
        get => FromHsv(_h, _s, _v);
        set { (_h, _s, _v) = ToHsv(value, _h); Refresh(raise: false); }
    }

    public ColorPicker()
    {
        Width = SquareW;

        // ---- saturation / brightness square
        _hueFill = new Rectangle();
        var white = new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var black = new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        _svThumb = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = .6 } };
        _svCanvas = new Canvas { ClipToBounds = false };
        _svCanvas.Children.Add(_svThumb);
        _square = new Border
        {
            Width = SquareW, Height = SquareH, CornerRadius = new CornerRadius(10), Focusable = true, Cursor = Cursors.Cross,
            Child = new Grid { Children = { _hueFill, white, black, _svCanvas } },
            ClipToBounds = false,
        };
        _square.Clip = new RectangleGeometry(new Rect(-8, -8, SquareW + 16, SquareH + 16));
        AutomationProperties.SetName(_square, "Saturation and brightness. Arrow keys adjust.");
        Hook(_square, p => { _s = Clamp01(p.X / SquareW); _v = 1 - Clamp01(p.Y / SquareH); });
        _square.KeyDown += (_, e) => Nudge(e, dx: e.Key == Key.Right ? 1 : e.Key == Key.Left ? -1 : 0, dy: e.Key == Key.Up ? 1 : e.Key == Key.Down ? -1 : 0,
            apply: (dx, dy, step) => { _s = Clamp01(_s + dx * step); _v = Clamp01(_v + dy * step); });
        Children.Add(_square);

        // ---- hue strip
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
        _hueThumb = new Border { Width = 8, Height = HueH + 6, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2), IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = .6 } };
        _hueCanvas = new Canvas();
        _hueCanvas.Children.Add(_hueThumb);
        _hueBar = new Border
        {
            Height = HueH, CornerRadius = new CornerRadius(8), Background = rainbow, Margin = new Thickness(0, 12, 0, 0),
            Focusable = true, Cursor = Cursors.Hand, Child = _hueCanvas,
        };
        AutomationProperties.SetName(_hueBar, "Hue. Left and right arrow keys adjust.");
        Hook(_hueBar, p => _h = Clamp01(p.X / SquareW) * 360);
        _hueBar.KeyDown += (_, e) => Nudge(e, dx: e.Key == Key.Right ? 1 : e.Key == Key.Left ? -1 : 0, dy: 0,
            apply: (dx, _, step) => _h = (_h + dx * step * 360 + 360) % 360);
        Children.Add(_hueBar);

        // ---- hex + R G B
        _swatchNew = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        _swatchNew.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _hex = Field("Hex", 74, 7);
        _r = Field("Red", 38, 3);
        _g = Field("Green", 38, 3);
        _b = Field("Blue", 38, 3);
        var entry = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var w in new[] { 34.0, 6, 74, 6, 38, 4, 38, 4, 38 }) entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        AddCol(entry, _swatchNew, 0, null);
        AddCol(entry, _hex, 2, "HEX");
        AddCol(entry, _r, 4, "R");
        AddCol(entry, _g, 6, "G");
        AddCol(entry, _b, 8, "B");
        Children.Add(entry);
        _hex.LostFocus += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } };
        foreach (var box in new[] { _r, _g, _b })
        {
            box.LostFocus += (_, _) => CommitRgb();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitRgb(); e.Handled = true; } };
        }

        // ---- recent
        var recentLabel = new TextBlock { Text = "Recent", Margin = new Thickness(0, 14, 0, 6) };
        recentLabel.SetResourceReference(StyleProperty, "Text.Caption");
        Children.Add(recentLabel);
        _recent = new WrapPanel();
        Children.Add(_recent);

        Loaded += (_, _) => Refresh(raise: false);
    }

    /// <summary>Recent colours, newest first ("#RRGGBB"). The owner persists them.</summary>
    public IReadOnlyList<string> Recent { get; private set; } = [];

    public void SetRecent(IEnumerable<string> colours)
    {
        Recent = colours.Where(c => c.Length == 7).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        _recent.Children.Clear();
        foreach (var hex in Recent) _recent.Children.Add(Swatch(hex, () => { Color = Parse(hex)!.Value; Raise(); }));
        if (Recent.Count == 0)
        {
            var none = new TextBlock { Text = "Colours you pick show up here." };
            none.SetResourceReference(StyleProperty, "Text.Caption");
            _recent.Children.Add(none);
        }
    }

    /// <summary>Remember the current colour as recently used (call when the user commits a choice).</summary>
    public void PushRecent() => SetRecent(new[] { Hex(Color) }.Concat(Recent));

    public static Border Swatch(string hex, Action onPick, double size = 26)
    {
        var sw = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 6, 6),
            Background = new SolidColorBrush(Parse(hex) ?? Colors.Transparent), BorderThickness = new Thickness(1),
            Focusable = true, Cursor = Cursors.Hand, ToolTip = hex,
        };
        sw.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        AutomationProperties.SetName(sw, $"Colour {hex}");
        sw.MouseLeftButtonUp += (_, _) => onPick();
        sw.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { onPick(); e.Handled = true; } };
        sw.GotKeyboardFocus += (_, _) => sw.SetResourceReference(Border.BorderBrushProperty, "Brush.FocusRing");
        sw.LostKeyboardFocus += (_, _) => sw.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return sw;
    }

    // ---------------------------------------------------------------- internals

    private void Hook(FrameworkElement el, Action<Point> set)
    {
        void Apply(MouseEventArgs e) { set(e.GetPosition(el)); Refresh(raise: true); }
        el.MouseLeftButtonDown += (_, e) => { el.Focus(); el.CaptureMouse(); Apply(e); e.Handled = true; };
        el.MouseMove += (_, e) => { if (el.IsMouseCaptured) Apply(e); };
        el.MouseLeftButtonUp += (_, _) => el.ReleaseMouseCapture();
    }

    private void Nudge(KeyEventArgs e, int dx, int dy, Action<int, int, double> apply)
    {
        if (dx == 0 && dy == 0) return;
        apply(dx, dy, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01);
        Refresh(raise: true);
        e.Handled = true;
    }

    private void Refresh(bool raise)
    {
        if (_updating) return;
        _updating = true;
        var c = Color;
        _hueFill.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_svThumb, _s * SquareW - 8);
        Canvas.SetTop(_svThumb, (1 - _v) * SquareH - 8);
        Canvas.SetLeft(_hueThumb, _h / 360 * SquareW - 4);
        Canvas.SetTop(_hueThumb, -3);
        _swatchNew.Background = new SolidColorBrush(c);
        if (!_hex.IsKeyboardFocused) _hex.Text = Hex(c);
        if (!_r.IsKeyboardFocused) _r.Text = c.R.ToString(CultureInfo.InvariantCulture);
        if (!_g.IsKeyboardFocused) _g.Text = c.G.ToString(CultureInfo.InvariantCulture);
        if (!_b.IsKeyboardFocused) _b.Text = c.B.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        if (raise) Raise();
    }

    private void Raise() => ColorChanged?.Invoke(Color);

    private void CommitHex()
    {
        var t = _hex.Text.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        if (Parse(t) is Color c) { Color = c; Raise(); }
        else _hex.Text = Hex(Color);
    }

    private void CommitRgb()
    {
        byte V(TextBox tb, byte fallback) => int.TryParse(tb.Text, out var n) ? (byte)Math.Clamp(n, 0, 255) : fallback;
        var cur = Color;
        Color = Color.FromRgb(V(_r, cur.R), V(_g, cur.G), V(_b, cur.B));
        Raise();
    }

    private static TextBox Field(string name, double width, int max)
    {
        var tb = new TextBox { Width = width, MaxLength = max, MinHeight = 32, MinWidth = 0, Padding = new Thickness(0, 5, 0, 5), FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(tb, name);
        return tb;
    }

    private static void AddCol(Grid g, FrameworkElement el, int col, string? caption)
    {
        UIElement child = el;
        if (caption is not null)
        {
            var cap = new TextBlock { Text = caption, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            cap.SetResourceReference(StyleProperty, "Text.Caption");
            child = new StackPanel { Children = { el, cap } };
        }
        Grid.SetColumn(child, col);
        g.Children.Add(child);
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color? Parse(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#') return null;
        return uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v) : null;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        byte B(double d) => (byte)Math.Round((d + m) * 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }

    /// <summary>RGB to HSV. Greys keep <paramref name="keepHue"/> so the hue strip doesn't jump to red.</summary>
    public static (double h, double s, double v) ToHsv(Color c, double keepHue = 0)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var h = keepHue;
        if (d > 0)
        {
            h = max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }
}
