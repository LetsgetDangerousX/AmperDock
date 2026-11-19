using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmperDock.Core;

/// <summary>How one dock behaves (stored in containers.behavior_json). Unknown or broken JSON gives the defaults.</summary>
public sealed record DockBehavior
{
    /// <summary>Slide the dock off the screen edge when the pointer leaves it; touch the edge to bring it back.</summary>
    public bool AutoHide { get; init; }

    /// <summary>Show live previews of an app's windows when the pointer rests on its icon.</summary>
    public bool Previews { get; init; } = true;

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
