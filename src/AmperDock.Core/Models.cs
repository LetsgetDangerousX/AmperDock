namespace AmperDock.Core;

/// <summary>Item types, matching the CHECK constraint on items.type.</summary>
public static class ItemTypes
{
    public const string Program = "program";
    public const string Folder = "folder";
    public const string Document = "document";
    public const string Url = "url";
    public const string Command = "command";
    public const string Widget = "widget";
    public const string Separator = "separator";
    public const string ContainerLink = "container_link";
    public const string Uwp = "uwp";
}

/// <summary>One thing on a dock. Mirrors the items table (only the columns the app uses so far).</summary>
public sealed record DockItem(
    string Id,
    string ContainerId,
    double SortOrder,
    string Type,
    string Label,
    string? Target,
    string? Arguments,
    string? StartIn,
    string? IconPath,
    bool RunAsAdmin,
    bool AllowMultiple);

/// <summary>What the app needs to create an item from a drop. The store assigns the id and order.</summary>
public sealed record NewItem(string Type, string Label, string? Target, string? Arguments = null, string? StartIn = null);
