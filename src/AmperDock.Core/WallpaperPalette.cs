namespace AmperDock.Core;

/// <summary>
/// Dock colours taken from a wallpaper: its strongest colour family becomes the dock's tint, and the
/// picture's overall brightness decides between a dark dock and a light one. Colours are "#RRGGBB".
/// </summary>
public sealed record WallpaperPalette(string Fill, string Fill2, string Outline, string Indicator, string Tint, bool Dark)
{
    /// <summary>
    /// Work out the palette from RGB pixels (3 bytes each, any order of rows). A small sample is plenty:
    /// the caller decodes the wallpaper at about 96 pixels wide.
    /// </summary>
    public static WallpaperPalette From(ReadOnlySpan<byte> rgb)
    {
        const int Buckets = 24;
        var weight = new double[Buckets];
        var hueX = new double[Buckets];
        var hueY = new double[Buckets];
        var sat = new double[Buckets];
        double lumaSum = 0;
        var n = rgb.Length / 3;
        if (n == 0) return Fallback(dark: true);

        for (var i = 0; i < n; i++)
        {
            double r = rgb[i * 3] / 255.0, g = rgb[i * 3 + 1] / 255.0, b = rgb[i * 3 + 2] / 255.0;
            lumaSum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
            var (h, s, v) = ToHsv(r, g, b);
            if (s < 0.22 || v < 0.22) continue;   // greys, near-black and near-white say little about the colour
            var w = s * s * v;                    // vivid, bright pixels count most
            var k = (int)(h / 360 * Buckets) % Buckets;
            weight[k] += w;
            hueX[k] += w * Math.Cos(h * Math.PI / 180);
            hueY[k] += w * Math.Sin(h * Math.PI / 180);
            sat[k] += w * s;
        }

        var dark = lumaSum / n < 0.5;
        // the strongest colour family: a bucket plus its two neighbours, so a colour split across a boundary still wins
        int best = -1;
        double bestW = 0;
        for (var k = 0; k < Buckets; k++)
        {
            var w = weight[k] + 0.6 * (weight[(k + 1) % Buckets] + weight[(k + Buckets - 1) % Buckets]);
            if (w > bestW) (best, bestW) = (k, w);
        }
        if (best < 0 || bestW < n * 0.004) return Fallback(dark);   // a black-and-white picture: stay lilac

        double x = 0, y = 0, sw = 0, ws = 0;
        foreach (var k in new[] { (best + Buckets - 1) % Buckets, best, (best + 1) % Buckets })
        {
            x += hueX[k]; y += hueY[k]; ws += sat[k]; sw += weight[k];
        }
        var hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        var vivid = Math.Clamp(ws / sw, 0.35, 0.9);
        return Make(hue, vivid, dark);
    }

    private static WallpaperPalette Fallback(bool dark) => Make(262, 0.45, dark);

    private static WallpaperPalette Make(double hue, double vivid, bool dark) => dark
        ? new WallpaperPalette(
            Hex(hue, 0.42 * vivid + 0.18, 0.24), Hex(hue + 18, 0.48 * vivid + 0.2, 0.34),
            Hex(hue, 0.4 * vivid + 0.15, 0.68), Hex(hue, 0.5 * vivid + 0.2, 0.96), Hex(hue, vivid, 0.85), true)
        : new WallpaperPalette(
            Hex(hue, 0.14 * vivid + 0.05, 0.98), Hex(hue + 18, 0.22 * vivid + 0.06, 0.95),
            Hex(hue, 0.35 * vivid + 0.12, 0.8), Hex(hue, 0.6 * vivid + 0.25, 0.68), Hex(hue, vivid, 0.8), false);

    public static (double h, double s, double v) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d % 6 + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static string Hex(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return $"#{(int)Math.Round((r + m) * 255):X2}{(int)Math.Round((g + m) * 255):X2}{(int)Math.Round((b + m) * 255):X2}";
    }
}
