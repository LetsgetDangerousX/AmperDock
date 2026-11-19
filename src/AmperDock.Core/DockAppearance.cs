using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmperDock.Core;

/// <summary>How the dock band is painted.</summary>
public static class DockStyles
{
    public const string Glass = "glass";        // tinted fill + top highlight + outline (default)
    public const string Solid = "solid";        // flat fill, no highlight
    public const string Gradient = "gradient";  // two-colour linear gradient
    public const string Outline = "outline";    // no fill, just a coloured outline
    public static readonly string[] All = [Glass, Solid, Gradient, Outline];
}

/// <summary>The outline of the dock band.</summary>
public static class DockShapes
{
    public const string Rounded = "rounded";   // a rounded rectangle (Roundness sets the corners)
    public const string Leaf = "leaf";         // pointed ends, a midrib and veins, a little stem
    public static readonly string[] All = [Rounded, Leaf];
}

/// <summary>Running indicators. Paw and dot are drawn in a colour; the rest are little pictures.</summary>
public static class DockIndicators
{
    public const string Paw = "paw", Dot = "dot";
    public static readonly string[] Drawn = [Paw, Dot];
    public static readonly string[] Pictures = ["mushroom", "star", "leaf", "pumpkin", "petal", "heart", "planet", "moon", "twinkle"];
    public static bool IsKnown(string? id) => id is not null && (Drawn.Contains(id) || Pictures.Contains(id));

    public const string Small = "small", Medium = "medium", Large = "large";
    public static readonly string[] Sizes = [Small, Medium, Large];

    /// <summary>How much bigger or smaller than the theme's indicator size.</summary>
    public static float SizeFactor(string? size) => size switch { Small => 0.7f, Large => 1.3f, _ => 1f };
}

/// <summary>
/// User-chosen look for one dock, stored in containers.appearance_json.
/// Null colours = follow the theme (light/dark). Opacity 0 = an invisible dock: only the icons show.
/// </summary>
public sealed record DockAppearance
{
    public string Style { get; init; } = DockStyles.Glass;

    /// <summary>0 = sharp corners, 1 = pill (radius = half the band height).</summary>
    public float Roundness { get; init; } = 0.5f;

    /// <summary>Fill colour "#RRGGBB", or null to use the theme's colour.</summary>
    public string? Color { get; init; }

    /// <summary>Gradient end colour (Gradient style only). Null = a lighter version of the fill.</summary>
    public string? Color2 { get; init; }

    /// <summary>Gradient direction in degrees: 0 = left to right, 90 = top to bottom.</summary>
    public float GradientAngle { get; init; } = 0;

    /// <summary>Background opacity, 0 to 100 percent.</summary>
    public int Opacity { get; init; } = 78;

    /// <summary>Outline on/off. Outline-only style always shows it.</summary>
    public bool Outline { get; init; } = true;

    /// <summary>Outline colour, or null for the theme's outline colour.</summary>
    public string? OutlineColor { get; init; }

    /// <summary>Outline width in DIPs, 0.5 to 6.</summary>
    public float OutlineWidth { get; init; } = 1;

    /// <summary>The dock theme this look started from (decorations come from it), or null for none.</summary>
    public string? Theme { get; init; }

    /// <summary>Show the theme's decorations (ears, snow, little pictures on the dock).</summary>
    public bool Decorations { get; init; } = true;

    /// <summary>Band outline: <see cref="DockShapes"/>.</summary>
    public string Shape { get; init; } = DockShapes.Rounded;

    /// <summary>Running indicator: <see cref="DockIndicators"/>.</summary>
    public string Indicator { get; init; } = DockIndicators.Paw;

    /// <summary>Colour for the paw or dot indicator, or null for the theme's accent.</summary>
    public string? IndicatorColor { get; init; }

    /// <summary>The dock's own colours follow the wallpaper (and change when it does).</summary>
    public bool MatchWallpaper { get; init; }

    /// <summary>Icon colours follow the wallpaper: how strongly to wash them toward its colour, 0 (off) to 100.
    /// Separate from <see cref="MatchWallpaper"/>: either can be on without the other.</summary>
    public int IconTint { get; init; }

    /// <summary>Reflections under the icons: 0 = off, else how strong (1 to 100).</summary>
    public int Reflection { get; init; }

    /// <summary>Running indicator size: small, medium or large (<see cref="DockIndicators.Sizes"/>).</summary>
    public string IndicatorSize { get; init; } = DockIndicators.Medium;

    public static readonly DockAppearance Default = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Lenient parse: unknown or broken JSON falls back to defaults rather than breaking the dock.</summary>
    public static DockAppearance FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return (JsonSerializer.Deserialize<DockAppearance>(json, Json) ?? Default).Normalised(); }
        catch (JsonException) { return Default; }
    }

    public DockAppearance Normalised() => this with
    {
        Style = DockStyles.All.Contains(Style) ? Style : DockStyles.Glass,
        Roundness = Math.Clamp(float.IsFinite(Roundness) ? Roundness : 0.5f, 0, 1),
        Opacity = Math.Clamp(Opacity, 0, 100),
        Color = Hex(Color),
        Color2 = Hex(Color2),
        OutlineColor = Hex(OutlineColor),
        GradientAngle = float.IsFinite(GradientAngle) ? ((GradientAngle % 360) + 360) % 360 : 0,
        OutlineWidth = Math.Clamp(float.IsFinite(OutlineWidth) ? OutlineWidth : 1, 0.5f, 6),
        Theme = string.IsNullOrWhiteSpace(Theme) ? null : Theme.Trim().ToLowerInvariant(),
        Shape = DockShapes.All.Contains(Shape) ? Shape : DockShapes.Rounded,
        Indicator = DockIndicators.IsKnown(Indicator) ? Indicator : DockIndicators.Paw,
        IndicatorColor = Hex(IndicatorColor),
        IndicatorSize = DockIndicators.Sizes.Contains(IndicatorSize) ? IndicatorSize : DockIndicators.Medium,
        Reflection = Math.Clamp(Reflection, 0, 100),
        IconTint = Math.Clamp(IconTint, 0, 100),
    };

    private static string? Hex(string? s) => IsHexColor(s) ? s!.ToUpperInvariant() : null;

    public static bool IsHexColor(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    /// <summary>Corner radius in pixels for a band of the given height.</summary>
    public float RadiusFor(float bandHeight) => Roundness * bandHeight / 2;
}
