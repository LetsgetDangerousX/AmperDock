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
    public string Colour { get; init; } = "lilac";            // see WidgetColours
    public string Text { get; init; } = "";                   // note: its text; countdown: its title
    public DateTime? Target { get; init; }                    // countdown: the moment it counts to (local time)
    public bool Clock24 { get; init; }                        // clock: 24-hour time

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
