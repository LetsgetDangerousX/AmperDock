using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmperDock.Core;

/// <summary>
/// User-chosen look for one dock, stored in containers.appearance_json.
/// Null colour = follow the theme (light/dark). Opacity 0 = an invisible dock: only the icons show.
/// </summary>
public sealed record DockAppearance
{
    /// <summary>0 = sharp corners, 1 = pill (radius = half the band height). Default matches the Lilac theme.</summary>
    public float Roundness { get; init; } = 0.5f;

    /// <summary>"#RRGGBB", or null to use the theme's colour for the current light/dark mode.</summary>
    public string? Color { get; init; }

    /// <summary>Background opacity, 0 to 100 percent.</summary>
    public int Opacity { get; init; } = 78;

    /// <summary>The 1px outline. Hidden automatically when Opacity is 0.</summary>
    public bool Outline { get; init; } = true;

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
        try
        {
            var a = JsonSerializer.Deserialize<DockAppearance>(json, Json) ?? Default;
            return a.Normalised();
        }
        catch (JsonException) { return Default; }
    }

    public DockAppearance Normalised() => this with
    {
        Roundness = Math.Clamp(float.IsFinite(Roundness) ? Roundness : 0.5f, 0, 1),
        Opacity = Math.Clamp(Opacity, 0, 100),
        Color = IsHexColor(Color) ? Color!.ToUpperInvariant() : null,
    };

    public static bool IsHexColor(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    /// <summary>Corner radius in pixels for a band of the given height.</summary>
    public float RadiusFor(float bandHeight) => Roundness * bandHeight / 2;
}
