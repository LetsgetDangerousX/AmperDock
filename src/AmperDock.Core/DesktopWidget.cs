using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmperDock.Core;

/// <summary>
/// A widget that lives on the desktop (a note, a clock, a countdown): where it sits, its colour, and
/// what it holds. All of them are kept together in the settings table under <see cref="SettingsKey"/>.
/// </summary>
public sealed record DesktopWidget
{
    public const string SettingsKey = "desktop.widgets";

    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Kind { get; init; } = "note";              // note | clock | countdown
    public double X { get; init; }                            // top-left, in screen pixels
    public double Y { get; init; }
    public string Colour { get; init; } = "lilac";            // a named colour (see WidgetColours) or any "#RRGGBB"
    public string? TextColour { get; init; }                  // note: "#RRGGBB", or null to suit the card
    public string? Font { get; init; }                        // note: a font family name, or null for the app's font
    public double TextSize { get; init; } = 15;               // note: text size in points
    public double Width { get; init; }                        // window size in DIPs after resizing, 0 = the widget's own size
    public double Height { get; init; }
    public string Text { get; init; } = "";                   // note: its text; countdown: its title
    public DateTime? Target { get; init; }                    // countdown: the moment it counts to (local time)
    public bool Clock24 { get; init; }                        // clock: 24-hour time
    public string? Place { get; init; }                       // weather: the place's name as found ("Paris, France")
    public double? Latitude { get; init; }                    // weather: where it's for
    public double? Longitude { get; init; }
    public bool Fahrenheit { get; init; }                     // weather: °F instead of °C

    /// <summary>What a WMO weather code (as weather services report it) means, in plain words, and which picture to show.</summary>
    public static (string words, string picture) WeatherCode(int code) => code switch
    {
        0 => ("Clear", "sun"),
        1 => ("Mostly clear", "sun"),
        2 => ("Partly cloudy", "partly"),
        3 => ("Cloudy", "cloud"),
        45 or 48 => ("Foggy", "fog"),
        >= 51 and <= 57 => ("Drizzle", "rain"),
        >= 61 and <= 67 => ("Rain", "rain"),
        >= 71 and <= 77 => ("Snow", "snow"),
        >= 80 and <= 82 => ("Showers", "rain"),
        85 or 86 => ("Snow showers", "snow"),
        >= 95 => ("Thunderstorm", "storm"),
        _ => ("Weather", "cloud"),
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<DesktopWidget> ListFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<DesktopWidget>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string ListToJson(IEnumerable<DesktopWidget> widgets) => JsonSerializer.Serialize(widgets, Json);

    /// <summary>The next 25 December at midnight (or this year's, if it hasn't come yet): a friendly first countdown.</summary>
    public static DateTime NextChristmas(DateTime now)
    {
        var christmas = new DateTime(now.Year, 12, 25);
        return now < christmas ? christmas : christmas.AddYears(1);
    }

    /// <summary>
    /// What a countdown shows: whole days left, then hours and minutes. Past the moment, all zero and
    /// <c>arrived</c> is true.
    /// </summary>
    public static (int days, int hours, int minutes, bool arrived) Remaining(DateTime target, DateTime now)
    {
        var left = target - now;
        if (left <= TimeSpan.Zero) return (0, 0, 0, true);
        return (left.Days, left.Hours, left.Minutes, false);
    }
}
