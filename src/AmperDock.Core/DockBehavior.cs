using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmperDock.Core;

/// <summary>How one dock behaves (stored in containers.behavior_json). Unknown or broken JSON gives the defaults.</summary>
public sealed record DockBehavior
{
    /// <summary>Reserve the dock's strip of the screen, so maximised windows stop at the dock instead of going under it.</summary>
    public bool ReserveSpace { get; init; }

    /// <summary>Slide the dock off the screen edge when the pointer leaves it; touch the edge to bring it back.</summary>
    public bool AutoHide { get; init; }

    /// <summary>
    /// How long the pointer has to rest at the screen edge before a hidden dock comes back, in milliseconds.
    /// 0 brings it back the moment the edge is touched; a short wait stops it popping up on the way to the Start button.
    /// </summary>
    public int RevealDelayMs { get; init; } = 600;

    /// <summary>Auto-hide: only the hotkey brings the dock back; the screen edge does nothing.</summary>
    public bool HotkeyOnly { get; init; }

    /// <summary>The hotkey that brings auto-hidden docks back (and tucks them away again): see DockHost.Hotkeys.</summary>
    public string Hotkey { get; init; } = "ctrl+alt+d";

    /// <summary>Keep the dock where it is: pressing and dragging on it never moves it (its Position menu still can).</summary>
    public bool LockPosition { get; init; }

    /// <summary>Show apps that are open but not pinned, after the pinned ones. Null: only the main dock does.</summary>
    public bool? ShowOpenApps { get; init; }

    /// <summary>Show live previews of an app's windows when the pointer rests on its icon.</summary>
    public bool Previews { get; init; } = true;

    /// <summary>
    /// A desktop shelf: it lives on the desktop behind your windows, and desktop icons dropped on it are
    /// tidied away from the desktop (hidden there, never moved) until they come off the shelf again.
    /// </summary>
    public bool DesktopShelf { get; init; }

    /// <summary>Desktop files this shelf has hidden from the desktop, so they can be put back exactly.</summary>
    public List<string> HiddenOnDesktop { get; init; } = [];

    /// <summary>Each tidied file's attributes from before the shelf touched it (path -> FileAttributes), restored exactly.</summary>
    public Dictionary<string, int> OriginalAttributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DockBehavior Default = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DockBehavior FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return JsonSerializer.Deserialize<DockBehavior>(json, Json) ?? Default; }
        catch (JsonException) { return Default; }
    }
}
