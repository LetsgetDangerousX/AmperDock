using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmperDock.Core;
using AmperDock.Render;

namespace AmperDock.App.Dock;

/// <summary>A picture on a dock theme, and where it sits. See tools/build_dockthemes.py for the meaning of each field.</summary>
public sealed record DecorInfo
{
    public string Sprite { get; init; } = "";
    public string From { get; init; } = "start";
    public float Offset { get; init; }
    public string Edge { get; init; } = "top";
    public float Dy { get; init; }
    public float[] Anchor { get; init; } = [0.5f, 0.5f];
    public float Size { get; init; } = 0.5f;
    public bool Behind { get; init; }
    public bool Upright { get; init; } = true;
    public bool Mirror { get; init; }
}

/// <summary>A built-in dock theme: a starting look plus its decorations.</summary>
public sealed record DockThemeInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Season { get; init; }
    public string Blurb { get; init; } = "";
    public string Preview { get; init; } = "";
    public JsonElement Look { get; init; }
    public bool Ears { get; init; }
    public bool Snow { get; init; }
    public List<DecorInfo> Decor { get; init; } = [];

    /// <summary>The theme's look as a DockAppearance (the JSON uses the same snake_case names).</summary>
    public DockAppearance Appearance => DockAppearance.FromJson(Look.GetRawText());
}

/// <summary>The built-in dock themes, from DockThemes/dockthemes.json next to the app (made by tools/build_dockthemes.py).</summary>
public sealed class DockThemeCatalog
{
    public Dictionary<string, string> Indicators { get; init; } = new();
    public List<DockThemeInfo> Themes { get; init; } = [];

    public static string Root => Path.Combine(AppContext.BaseDirectory, "DockThemes");

    private static DockThemeCatalog? s_loaded;

    /// <summary>Loaded once; an empty catalogue if the files are missing (the dock still works, just plainer).</summary>
    public static DockThemeCatalog Shared => s_loaded ??= Load();

    private static DockThemeCatalog Load()
    {
        try
        {
            var path = Path.Combine(Root, "dockthemes.json");
            if (!File.Exists(path)) return new DockThemeCatalog();
            return JsonSerializer.Deserialize<DockThemeCatalog>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
            }) ?? new DockThemeCatalog();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            App.Log(ex);
            return new DockThemeCatalog();
        }
    }

    public DockThemeInfo? Find(string? id) => id is null ? null : Themes.FirstOrDefault(t => t.Id == id);

    public static string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static ImageSource? Image(string relative)
    {
        var path = FullPath(relative);
        if (!File.Exists(path)) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Premultiplied BGRA pixels of one of the theme pictures, or null if it's missing.</summary>
    public static (int w, int h, byte[] px)? Pixels(string relative)
    {
        var path = FullPath(relative);
        if (!File.Exists(path)) return null;
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var px = new byte[w * h * 4];
        converted.CopyPixels(px, w * 4, 0);
        return (w, h, px);
    }

    /// <summary>
    /// Where a decoration goes for a band at <paramref name="band"/> (dock space). Returns its square in
    /// dock space; the caller converts it to window pixels.
    /// </summary>
    public static RectF Place(DecorInfo d, RectF band)
    {
        var h = band.H;
        var along = d.From == "end" ? band.Right - d.Offset * h : band.X + d.Offset * h;
        var across = (d.Edge == "bottom" ? band.Bottom : band.Y) + d.Dy * h;
        var size = d.Size * h;
        var ax = d.Mirror ? 1 - d.Anchor[0] : d.Anchor[0];
        return new RectF(along - ax * size, across - d.Anchor[1] * size, size, size);
    }

    /// <summary>
    /// The 2x2 turn that makes art drawn for a bottom dock face away from the screen on other edges
    /// (a flip for top, a quarter turn or a transpose for the sides), plus an optional left-right mirror.
    /// Upright art only gets the mirror.
    /// </summary>
    public static Matrix3x2 Orient(DockEdge edge, bool upright, bool mirror)
    {
        var m = mirror ? new Matrix3x2(-1, 0, 0, 1, 0, 0) : Matrix3x2.Identity;
        if (upright) return m;
        var turn = edge switch
        {
            DockEdge.Top => new Matrix3x2(1, 0, 0, -1, 0, 0),
            DockEdge.Left => new Matrix3x2(0, 1, -1, 0, 0, 0),
            DockEdge.Right => new Matrix3x2(0, 1, 1, 0, 0, 0),
            _ => Matrix3x2.Identity,
        };
        return m * turn;
    }
}
