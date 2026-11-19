using System.Runtime.InteropServices;
using System.Text;

namespace AmperDock.Shell;

/// <summary>
/// Hand-written P/Invoke for the vertical slice. Only documented, public Win32 APIs.
/// (architecture.md suggests CsWin32. Moving to it is a mechanical swap once the surface area grows.)
/// </summary>
public static partial class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    public const int SW_RESTORE = 9, SW_MINIMIZE = 6, SW_SHOWNA = 8;
    public const uint GW_OWNER = 4;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int DWMWA_CLOAKED = 14;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint GetWindow(nint hwnd, uint cmd);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtr(nint hwnd, int index);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] public static partial int GetWindowTextLength(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumChildWindows(nint parent, EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint hwnd, uint flags);
    public const uint GA_ROOT = 2;

    public static string ClassOf(nint hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("dwmapi.dll")] public static partial int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);

    [LibraryImport("kernel32.dll")] public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);

    // ---- GDI, for reading shell-icon HBITMAPs
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public nint bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [DllImport("gdi32.dll")] public static extern int GetObject(nint h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint h);
    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint hdc);

    // ---- drag & drop of files (WM_DROPFILES)
    public const int WM_DROPFILES = 0x0233;
    [LibraryImport("shell32.dll")] public static partial void DragAcceptFiles(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    public static extern uint DragQueryFile(nint hDrop, uint index, StringBuilder? file, uint size);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DragQueryPoint(nint hDrop, out POINT pt);
    [LibraryImport("shell32.dll")] public static partial void DragFinish(nint hDrop);

    /// <summary>Lets Explorer (medium integrity) drop onto us even if UIPI would filter the messages.</summary>
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint changeInfo);
    public const uint MSGFLT_ALLOW = 1, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049;
}
