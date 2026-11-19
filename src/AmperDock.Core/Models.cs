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

/// <summary>A top-level dock (a row of the containers table with kind = 'dock').</summary>
public sealed record DockInfo(string Id, string Name, bool Enabled);

/// <summary>
/// An item's custom icon, stored in items.icon_path the way Windows writes icon locations:
/// "file" for an image or .ico, or "file,index" for an icon inside an .exe or .dll.
/// </summary>
public readonly record struct IconRef(string File, int Index)
{
    private static readonly string[] Containers = [".exe", ".dll", ".icl", ".ico", ".cpl", ".mun", ".ocx", ".scr"];

    public static IconRef? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().Trim('"');
        // a trailing ",N" is an index only if what precedes it looks like an icon container (paths may contain commas)
        var comma = v.LastIndexOf(',');
        if (comma > 0 && int.TryParse(v.AsSpan(comma + 1).Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            var file = v[..comma].Trim().Trim('"');
            if (Containers.Contains(Path.GetExtension(file).ToLowerInvariant())) return new IconRef(file, index);
        }
        return new IconRef(v, 0);
    }

    /// <summary>True when the icon lives inside a file that holds icons by index (exe, dll, ico...).</summary>
    public bool IsIconResource => Containers.Contains(Path.GetExtension(File).ToLowerInvariant());

    public override string ToString() => IsIconResource ? $"{File},{Index.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : File;
}
