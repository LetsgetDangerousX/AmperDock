namespace AmperDock.Core;

/// <summary>Dock metrics in device pixels (already multiplied by the DPI scale). Defaults come from tokens.json "dock".</summary>
public sealed record DockMetrics(
    float IconSize = 48,
    float MagnifiedSize = 80,
    float MagnifyRangeIcons = 2.5f,
    float Spacing = 8,
    float PadX = 12,
    float PadY = 10,
    float IndicatorSize = 10,
    float IndicatorGap = 3,
    float Radius = 22)
{
    public DockMetrics Scale(float s) => new(IconSize * s, MagnifiedSize * s, MagnifyRangeIcons, Spacing * s,
        PadX * s, PadY * s, IndicatorSize * s, IndicatorGap * s, Radius * s);

    /// <summary>Height of the dock band at rest: padding + icon + room for the running indicator.</summary>
    public float BandHeight => PadY * 2 + IconSize + IndicatorSize + IndicatorGap;

    /// <summary>Window height needed so magnified icons (and a label above them) fit.</summary>
    public float WindowHeight(float labelRoom) => BandHeight + (MagnifiedSize - IconSize) + labelRoom;
}

public readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public float CenterX => X + W / 2;
    public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;
}

/// <summary>
/// Pure layout for a horizontal bottom dock. Magnification uses each icon's REST centre for the
/// distance to the pointer, so the curve doesn't feed back on itself as icons grow.
/// </summary>
public static class DockLayout
{
    /// <summary>Rest-state centres (x) of n icons, centred on <paramref name="centerX"/>.</summary>
    public static float[] RestCenters(int n, DockMetrics m, float centerX)
    {
        var centers = new float[n];
        if (n == 0) return centers;
        var total = n * m.IconSize + (n - 1) * m.Spacing;
        var x = centerX - total / 2 + m.IconSize / 2;
        for (var i = 0; i < n; i++, x += m.IconSize + m.Spacing) centers[i] = x;
        return centers;
    }

    /// <summary>Target icon sizes for a pointer at <paramref name="pointerX"/> (null = pointer outside).</summary>
    public static float[] TargetSizes(float[] restCenters, DockMetrics m, float? pointerX)
    {
        var sizes = new float[restCenters.Length];
        var range = m.MagnifyRangeIcons * (m.IconSize + m.Spacing);
        for (var i = 0; i < sizes.Length; i++)
        {
            var k = 0f;
            if (pointerX is float px)
            {
                var d = MathF.Abs(px - restCenters[i]);
                if (d < range) k = (MathF.Cos(MathF.PI * d / range) + 1) / 2;   // 1 under the pointer, 0 at the range edge
            }
            sizes[i] = m.IconSize + (m.MagnifiedSize - m.IconSize) * k;
        }
        return sizes;
    }

    /// <summary>Move current sizes toward targets. Frame-rate independent exponential smoothing.</summary>
    public static bool Approach(float[] current, float[] target, float dtSeconds, float speed = 18f)
    {
        var t = 1 - MathF.Exp(-speed * dtSeconds);
        var moving = false;
        for (var i = 0; i < current.Length; i++)
        {
            var diff = target[i] - current[i];
            if (MathF.Abs(diff) < 0.25f) { current[i] = target[i]; continue; }
            current[i] += diff * t;
            moving = true;
        }
        return moving;
    }

    /// <summary>Icon rectangles (bottom-aligned on the baseline) and the background band, for given sizes.</summary>
    public static (RectF[] icons, RectF background) Arrange(float[] sizes, DockMetrics m, float centerX, float windowHeight)
    {
        var n = sizes.Length;
        var icons = new RectF[n];
        var baseline = windowHeight - m.PadY - m.IndicatorSize - m.IndicatorGap;   // bottom edge of the icons
        var total = n == 0 ? m.IconSize : sizes.Sum() + (n - 1) * m.Spacing;
        var x = centerX - total / 2;
        for (var i = 0; i < n; i++)
        {
            icons[i] = new RectF(x, baseline - sizes[i], sizes[i], sizes[i]);
            x += sizes[i] + m.Spacing;
        }
        var bgTop = windowHeight - m.BandHeight;
        var bg = new RectF(centerX - total / 2 - m.PadX, bgTop, total + m.PadX * 2, m.BandHeight);
        return (icons, bg);
    }

    /// <summary>Index an item would be inserted at if dropped at x (rest layout).</summary>
    public static int InsertIndex(float[] restCenters, float x)
    {
        var i = 0;
        while (i < restCenters.Length && x > restCenters[i]) i++;
        return i;
    }

    /// <summary>Index of the icon under the point, or -1.</summary>
    public static int HitTest(RectF[] icons, float x, float y, float slop = 0)
    {
        for (var i = 0; i < icons.Length; i++)
        {
            var r = icons[i];
            if (x >= r.X - slop && x < r.Right + slop && y >= r.Y && y < r.Bottom + slop * 2) return i;
        }
        return -1;
    }
}
