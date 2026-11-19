namespace AmperDock.Core;

/// <summary>
/// Which open windows belong to which dock item. Two passes, like the taskbar's grouping:
/// 1. a window with an app id (AUMID) goes to the item with that app id;
/// 2. the windows left over go to an item that opens the same program file.
/// So a pinned web app gets its own windows, and the browser item keeps the rest. Windows that match
/// nothing are returned as leftovers (for showing open apps that aren't pinned).
/// </summary>
public static class TaskMatch
{
    public readonly record struct ItemKey(string ItemId, string? AppId, string? Exe);

    public static (Dictionary<string, List<T>> byItem, List<T> leftovers) Assign<T>(
        IReadOnlyList<ItemKey> items, IReadOnlyList<T> windows, Func<T, string?> appId, Func<T, string?> exe)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var byAppId = new Dictionary<string, string>(cmp);
        var byExe = new Dictionary<string, string>(cmp);
        foreach (var k in items)
        {
            if (k.AppId is not null) byAppId.TryAdd(k.AppId, k.ItemId);   // the first item wins if two are the same app
            if (k.Exe is not null) byExe.TryAdd(k.Exe, k.ItemId);
        }

        var result = new Dictionary<string, List<T>>();
        var leftovers = new List<T>();
        void Add(string id, T w)
        {
            if (!result.TryGetValue(id, out var list)) result[id] = list = new List<T>();
            list.Add(w);
        }
        // 3. Windows 11 inbox apps: C:\Windows\System32\notepad.exe opens the Store Notepad, whose window
        //    belongs to ...\WindowsApps\...\Notepad.exe. A packaged window nobody claimed goes to an item
        //    whose program has the same file name.
        var byFileName = new Dictionary<string, string>(cmp);
        foreach (var k in items)
            if (k.Exe is not null) byFileName.TryAdd(FileName(k.Exe), k.ItemId);

        foreach (var w in windows)
        {
            if (appId(w) is { } a && byAppId.TryGetValue(a, out var id)) Add(id, w);
            else if (exe(w) is { } e && byExe.TryGetValue(e, out id)) Add(id, w);
            else if (appId(w) is not null && exe(w) is { } pe && byFileName.TryGetValue(FileName(pe), out id)) Add(id, w);
            else leftovers.Add(w);
        }
        return (result, leftovers);
    }

    private static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i < 0 ? path : path[(i + 1)..];
    }
}
