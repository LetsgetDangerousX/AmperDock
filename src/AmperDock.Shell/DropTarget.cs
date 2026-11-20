using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace AmperDock.Shell;

/// <summary>
/// Accepts drag and drop onto a window the full OLE way, so the dock takes apps dragged from the
/// Start menu (which hands over shell items, not files) as well as files and folders from Explorer.
/// Every dropped thing becomes a "target": a file or folder path, or shell:AppsFolder\&lt;app id&gt;.
/// </summary>
[ComVisible(true)]
public sealed class DropTarget : DropTarget.IOleDropTarget
{
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject data, int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragOver(int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject data, int keys, POINTL pt, ref int effect);
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINTL { public int X, Y; }

    private const int DROPEFFECT_NONE = 0, DROPEFFECT_LINK = 4, S_OK = 0;
    private const short CF_HDROP = 15;
    private static readonly short CF_IDLIST = (short)RegisterClipboardFormat("Shell IDList Array");
    private const string AppsFolderParsing = "::{4234D49B-0245-4DF3-B780-3893943456E1}\\";

    private readonly Action<IReadOnlyList<string>, int, int> _dropped;
    private readonly Action<int, int>? _over;
    private bool _accept;

    /// <param name="dropped">Called with the dropped targets and the screen point.</param>
    /// <param name="over">Called as the drag moves over the window (screen point), e.g. to magnify icons.</param>
    public DropTarget(Action<IReadOnlyList<string>, int, int> dropped, Action<int, int>? over = null)
    {
        _dropped = dropped;
        _over = over;
    }

    /// <summary>Start taking drops on <paramref name="hwnd"/>. False if Windows refused (the caller can fall back to WM_DROPFILES).</summary>
    public bool Register(nint hwnd)
    {
        OleInitialize(0);   // already done on a WPF thread; harmless to ask again
        return RegisterDragDrop(hwnd, this) == S_OK;
    }

    public static void Revoke(nint hwnd) => RevokeDragDrop(hwnd);

    public int DragEnter(IDataObject data, int keys, POINTL pt, ref int effect)
    {
        _accept = Has(data, CF_IDLIST) || Has(data, CF_HDROP);
        effect = _accept ? DROPEFFECT_LINK : DROPEFFECT_NONE;
        if (_accept) _over?.Invoke(pt.X, pt.Y);
        return S_OK;
    }

    public int DragOver(int keys, POINTL pt, ref int effect)
    {
        effect = _accept ? DROPEFFECT_LINK : DROPEFFECT_NONE;
        if (_accept) _over?.Invoke(pt.X, pt.Y);
        return S_OK;
    }

    public int DragLeave() => S_OK;

    public int Drop(IDataObject data, int keys, POINTL pt, ref int effect)
    {
        var targets = Targets(data);
        effect = targets.Count > 0 ? DROPEFFECT_LINK : DROPEFFECT_NONE;
        if (targets.Count > 0) _dropped(targets, pt.X, pt.Y);
        return S_OK;
    }

    private static bool Has(IDataObject data, short format)
    {
        var f = new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        return data.QueryGetData(ref f) == S_OK;
    }

    /// <summary>What was dropped: shell items first (they name Start menu apps exactly), then plain files.</summary>
    public static List<string> Targets(IDataObject data)
    {
        var list = FromIdList(data);
        if (list.Count == 0) list = FromHDrop(data);
        return list;
    }

    private static List<string> FromIdList(IDataObject data)
    {
        var result = new List<string>();
        if (!Has(data, CF_IDLIST)) return result;
        var f = new FORMATETC { cfFormat = CF_IDLIST, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        data.GetData(ref f, out var medium);
        try
        {
            var p = GlobalLock(medium.unionmember);
            if (p == 0) return result;
            try
            {
                // CIDA: a count, then offsets to the parent folder's ID list and each item's (relative) ID list
                var count = Marshal.ReadInt32(p);
                var parent = p + Marshal.ReadInt32(p, 4);
                // items from Start → All apps live in the apps folder and are named by their bare app id
                var inAppsFolder = SHGetNameFromIDList(parent, 0x80028000, out var parentPtr) == S_OK
                    && (Marshal.PtrToStringUni(parentPtr) ?? "").Contains("4234D49B-0245-4DF3-B780-3893943456E1", StringComparison.OrdinalIgnoreCase);
                if (parentPtr != 0) Marshal.FreeCoTaskMem(parentPtr);
                for (var i = 0; i < count; i++)
                {
                    var child = p + Marshal.ReadInt32(p, 8 + 4 * i);
                    var full = ILCombine(parent, child);
                    if (full == 0) continue;
                    try
                    {
                        if (SHGetNameFromIDList(full, 0x80028000 /* SIGDN_DESKTOPABSOLUTEPARSING */, out var namePtr) != S_OK) continue;
                        var name = Marshal.PtrToStringUni(namePtr) ?? "";
                        Marshal.FreeCoTaskMem(namePtr);
                        if (name.StartsWith(AppsFolderParsing, StringComparison.OrdinalIgnoreCase))
                            result.Add(AppIds.AppsFolderPrefix + name[AppsFolderParsing.Length..]);
                        else if (inAppsFolder && name.Length > 0)
                            result.Add(AppIds.AppsFolderPrefix + name);
                        else if (Path.IsPathFullyQualified(name))
                            result.Add(name);
                    }
                    finally { ILFree(full); }
                }
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
        return result;
    }

    private static List<string> FromHDrop(IDataObject data)
    {
        var result = new List<string>();
        if (!Has(data, CF_HDROP)) return result;
        var f = new FORMATETC { cfFormat = CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        data.GetData(ref f, out var medium);
        try
        {
            var count = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var len = DragQueryFile(medium.unionmember, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                DragQueryFile(medium.unionmember, i, sb, len + 1);
                result.Add(sb.ToString());
            }
        }
        finally { ReleaseStgMedium(ref medium); }
        return result;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bc, ref Guid bhid, ref Guid riid, out nint ppv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    /// <summary>
    /// For tests: the drag data Windows itself would hand over for an item (the same data a drag from the
    /// Start menu or Explorer carries), read the way a drop is read.
    /// </summary>
    public static List<string> TargetsOf(string parsingName)
    {
        var iidItem = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(parsingName, 0, ref iidItem, out var item) != S_OK || item is null) return [];
        try
        {
            var bhid = new Guid("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");   // BHID_DataObject
            var iidData = typeof(IDataObject).GUID;
            if (item.BindToHandler(0, ref bhid, ref iidData, out var p) != S_OK || p == 0) return [];
            var data = (IDataObject)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            return Targets(data);
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint hwnd, IOleDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint hwnd);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint h);
    [DllImport("shell32.dll")] private static extern nint ILCombine(nint parent, nint child);
    [DllImport("shell32.dll")] private static extern void ILFree(nint pidl);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(nint pidl, uint sigdn, out nint name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(nint drop, uint index, StringBuilder? file, uint size);
}
