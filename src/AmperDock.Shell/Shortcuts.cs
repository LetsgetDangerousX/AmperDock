using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using AmperDock.Core;

namespace AmperDock.Shell;

/// <summary>Turns dropped paths into new dock items, and resolves .lnk shortcuts to their target exe.</summary>
public static class Shortcuts
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, nint pfd, uint flags);
        // The remaining methods are unused; the vtable order above is all that matters for GetPath.
    }

    /// <summary>The exe/file a .lnk points at, or null (e.g. shortcuts to Store apps have no path).</summary>
    public static string? ResolveLink(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, 0, 0);
            var path = sb.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (COMException) { return null; }
        finally { if (link is not null) Marshal.ReleaseComObject(link); }
    }

    /// <summary>Executable path that identifies an item's running windows (null if it isn't a program).</summary>
    public static string? ExeFor(DockItem item)
    {
        if (item.Target is null) return null;
        var t = item.Target;
        if (t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) t = ResolveLink(t) ?? t;
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(t) : null;
    }

    /// <summary>A new item for a path dropped from Explorer, the desktop or the Start menu.</summary>
    public static NewItem FromDroppedPath(string path)
    {
        if (Directory.Exists(path))
            return new NewItem(ItemTypes.Folder, LabelFor(path), path);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var type = ext switch
        {
            ".exe" or ".lnk" or ".appref-ms" => ItemTypes.Program,
            ".url" => ItemTypes.Url,
            _ => ItemTypes.Document,
        };
        return new NewItem(type, LabelFor(path), path, StartIn: ext == ".exe" ? Path.GetDirectoryName(path) : null);
    }

    private static string LabelFor(string path)
    {
        // programs carry a friendly name in their version info ("Notepad", not "notepad")
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            var desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) return desc;
        }
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length <= 3) return trimmed;                     // a drive root like C:\
        var name = Directory.Exists(trimmed) ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
