using System.Runtime.InteropServices;
using System.Text;
using AmperDock.Core;

namespace AmperDock.Shell;

/// <summary>An app from the Start menu's full app list (shell:AppsFolder), Store apps included.</summary>
public sealed record InstalledApp(string Name, string AppId)
{
    /// <summary>
    /// What a dock item stores to open it: the program or link itself when the app list names one
    /// (some desktop apps and web links are listed by their path), otherwise the shell:AppsFolder name.
    /// </summary>
    public string Target =>
        AppId.Contains("://", StringComparison.Ordinal) || (Path.IsPathFullyQualified(AppId) && File.Exists(AppId))
            ? AppId
            : AppIds.AppsFolderPrefix + AppId;
}

/// <summary>What identifies a dock item's windows: an app id (AUMID), an exe path, or both.</summary>
public readonly record struct ItemKeys(string? AppId, string? Exe);

/// <summary>
/// Application User Model IDs (AUMIDs): the ids Windows uses to group taskbar buttons. Store apps
/// are only identifiable this way (their windows belong to ApplicationFrameHost.exe). Reads them through
/// the documented property-store APIs and GetApplicationUserModelId.
/// </summary>
public static class AppIds
{
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    // ---- property store interop

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt, r1, r2, r3; public nint p, p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, nint bindCtx, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? id);

    private static readonly PROPERTYKEY AppUserModelId = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    private static readonly PROPERTYKEY LinkTargetParsingPath = new() { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 };
    private static Guid IID_IPropertyStore = typeof(IPropertyStore).GUID;

    private static string? ReadString(IPropertyStore? store, PROPERTYKEY key)
    {
        if (store is null) return null;
        try
        {
            if (store.GetValue(ref key, out var pv) < 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ && pv.p != 0 ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The app id a window was given explicitly (browsers' web apps, Store app frames), or null.</summary>
    public static string? ForWindow(nint hwnd)
    {
        try
        {
            return SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out var store) < 0 ? null : Clean(ReadString(store, AppUserModelId));
        }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    /// <summary>The app id of a packaged (Store/MSIX) process, or null for ordinary desktop programs.</summary>
    public static string? ForProcess(nint processHandle)
    {
        uint len = 0;
        if (GetApplicationUserModelId(processHandle, ref len, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */ || len == 0) return null;
        var sb = new StringBuilder((int)len);
        return GetApplicationUserModelId(processHandle, ref len, sb) == 0 ? Clean(sb.ToString()) : null;
    }

    private static string? FromParsingName(string path, PROPERTYKEY key)
    {
        try
        {
            return SHGetPropertyStoreFromParsingName(path, 0, 0, ref IID_IPropertyStore, out var store) < 0 ? null : Clean(ReadString(store, key));
        }
        catch (COMException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// What identifies the windows of an item's target:
    /// shell:AppsFolder\X → app id X (plus the program file, for desktop apps listed there);
    /// a .lnk → the app id stamped on the shortcut (web apps) and the program it points at; an .exe → itself.
    /// </summary>
    public static ItemKeys KeysFor(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return default;
        var t = target.Trim().Trim('"');
        if (t.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = Clean(t[AppsFolderPrefix.Length..]);
            return new ItemKeys(id, ExeOrNull(FromParsingName(t, LinkTargetParsingPath)));
        }
        if (t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return new ItemKeys(FromParsingName(t, AppUserModelId), ExeOrNull(Shortcuts.ResolveLink(t)));
        return new ItemKeys(null, ExeOrNull(t));
    }

    private static string? ExeOrNull(string? path)
    {
        if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    // ---- the app list (Start menu → All apps)

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bc, ref Guid bhid, ref Guid riid, out nint ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attribs);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems copy);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private const uint SIGDN_NORMALDISPLAY = 0, SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    private static string? Name(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var p) < 0 || p == 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// Every app in Start → All apps, sorted by name. Store apps, desktop programs and web apps alike.
    /// Slow-ish (a second on a busy PC); call it off the UI thread.
    /// </summary>
    public static List<InstalledApp> InstalledApps()
    {
        var list = new List<InstalledApp>();
        var iidItem = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName("shell:AppsFolder", 0, ref iidItem, out var folder) < 0 || folder is null) return list;
        try
        {
            var bhid = BHID_EnumItems;
            var iidEnum = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(0, ref bhid, ref iidEnum, out var pEnum) < 0 || pEnum == 0) return list;
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);
            try
            {
                while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1 && item is not null)
                {
                    try
                    {
                        var name = Clean(Name(item, SIGDN_NORMALDISPLAY));
                        var id = Clean(Name(item, SIGDN_PARENTRELATIVEPARSING));
                        if (name is not null && id is not null) list.Add(new InstalledApp(name, id));
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally { Marshal.ReleaseComObject(folder); }
        return list.DistinctBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The display name Windows gives an app id (for labels), or null.</summary>
    public static string? DisplayName(string appId)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(AppsFolderPrefix + appId, 0, ref iid, out var item) < 0 || item is null) return null;
        try { return Clean(Name(item, SIGDN_NORMALDISPLAY)); }
        finally { Marshal.ReleaseComObject(item); }
    }
}
