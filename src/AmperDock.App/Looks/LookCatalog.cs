using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AmperDock.App.Looks;

public sealed record CursorSetInfo(string Id, string Name, string Arrow, string Hand,
    [property: JsonPropertyName("preview_arrow")] string PreviewArrow, [property: JsonPropertyName("preview_hand")] string PreviewHand);

/// <summary>An extra wallpaper that only appears in the AmperDock slideshow (not tied to a theme pack).</summary>
public sealed record SlideshowWallpaperInfo(string Id, string Name, string Wallpaper);

public sealed record FolderIconInfo(string Id, string Name, string Icon, string Preview, string? Style = null);

/// <summary>A cursor trail and how its particles move. Units: DIPs and seconds. Ranges are [min, max].</summary>
public sealed record TrailInfo(string Id, string Name, string Tag, string[] Sprites, string Preview,
    float Spacing, float Life, float[] Size)
{
    public int Count { get; init; } = 1;
    public float Jitter { get; init; }
    public float[] Vx { get; init; } = [0, 0];
    public float[] Vy { get; init; } = [0, 0];
    public float[] Rot { get; init; } = [0, 0];
    public float[] Spin { get; init; } = [0, 0];
    public float Gravity { get; init; }
    public float Sway { get; init; }
    public bool Align { get; init; }
    public float Alternate { get; init; }
    public bool Flap { get; init; }
    public bool Pop { get; init; }
    public bool Shrink { get; init; }
}

/// <summary>The built-in look packs, from LookPacks/lookpacks.json next to the app (made by tools/build_lookpacks.py).</summary>
public sealed class LookCatalog
{
    [JsonPropertyName("cursor_sets")] public List<CursorSetInfo> CursorSets { get; init; } = [];
    [JsonPropertyName("folder_icons")] public List<FolderIconInfo> FolderIcons { get; init; } = [];
    public List<TrailInfo> Trails { get; init; } = [];
    [JsonPropertyName("theme_packs")] public List<ThemePackInfo> ThemePacks { get; init; } = [];
    [JsonPropertyName("icon_library")] public List<FolderIconInfo> IconLibrary { get; init; } = [];
    /// <summary>
    /// What a folder can wear: the folder icons, then the 3D set and the Planet Pals from the icon library.
    /// The library's candy pictures (each candy turned several ways) are left out: the four round candies are
    /// already folder icons. Library ids get a "lib-" prefix, since some ("planet", "book") are folder ids too.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<FolderIconInfo> FolderChoices => _folderChoices ??= [.. FolderIcons,
        .. IconLibrary.Where(i => i.Style != "Candy").Select(i => i with { Id = "lib-" + i.Id })];
    private List<FolderIconInfo>? _folderChoices;

    [JsonPropertyName("slideshow_wallpapers")] public List<SlideshowWallpaperInfo> SlideshowWallpapers { get; init; } = [];

    public static string Root => Path.Combine(AppContext.BaseDirectory, "LookPacks");

    public static LookCatalog Load()
    {
        var path = Path.Combine(Root, "lookpacks.json");
        if (!File.Exists(path)) return new LookCatalog();
        return JsonSerializer.Deserialize<LookCatalog>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) ?? new LookCatalog();
    }

    public static string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static ImageSource Image(string relative)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(FullPath(relative));
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Premultiplied BGRA pixels of a PNG, for the GPU sprite renderer.</summary>
    public static (int w, int h, byte[] px) Pixels(string relative)
    {
        var decoder = BitmapDecoder.Create(new Uri(FullPath(relative)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var px = new byte[w * h * 4];
        converted.CopyPixels(px, w * 4, 0);
        return (w, h, px);
    }
}
