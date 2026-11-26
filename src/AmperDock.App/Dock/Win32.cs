using System.Runtime.InteropServices;

namespace AmperDock.App.Dock;

/// <summary>Window-creation and input P/Invokes for the raw dock window.</summary>
internal static partial class Win32
{
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int SW_SHOWNA = 8;
    public const uint WM_DESTROY = 0x2, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONUP = 0x208, WM_MOUSELEAVE = 0x2A3, WM_CAPTURECHANGED = 0x215,
        WM_DPICHANGED = 0x2E0, WM_DISPLAYCHANGE = 0x7E, WM_SETTINGCHANGE = 0x1A, WM_DROPFILES = 0x233,
        WM_MOUSEACTIVATE = 0x21, WM_NCHITTEST = 0x84;
    public const int MA_NOACTIVATE = 3;
    public const uint TME_LEAVE = 2;
    public const uint TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTBUTTON = 0x2, MF_STRING = 0, MF_SEPARATOR = 0x800, MF_GRAYED = 1;
    public const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
    public const uint WM_WINDOWPOSCHANGING = 0x46;

    // app bars: reserving a strip of the screen, as the taskbar does
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABN_POSCHANGED = 1;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public const uint WM_APPBAR = 0x8000 + 0x41;   // WM_APP + n: Windows tells the dock when other bars move
    [DllImport("shell32.dll")] public static extern nint SHAppBarMessage(uint msg, ref APPBARDATA data);
    public static readonly nint HWND_BOTTOM = 1, HWND_NOTOPMOST = -2;
    public const int GWLP_HWNDPARENT = -8;
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public nint hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindow(string? cls, string? title);
    [LibraryImport("shell32.dll")] public static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h,
        nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint mon, ref MONITORINFO mi);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial nint GetCapture();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hwnd, out RECT r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ScreenToClient(nint hwnd, ref POINT p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ClientToScreen(nint hwnd, ref POINT p);
    [LibraryImport("user32.dll")] public static partial int SetWindowRgn(nint hwnd, nint rgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [LibraryImport("gdi32.dll")] public static partial nint CreateRectRgn(int l, int t, int r, int b);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(nint menu, uint flags, nint id, string? text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] public static partial nint GetModuleHandle(nint name);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] public static partial nint LoadCursor(nint inst, nint id);

    // ---- monitors, cursors, keyboard, submenus (dock moving)
    public const uint WM_SETCURSOR = 0x20, MF_POPUP = 0x10, MF_CHECKED = 0x8, MONITOR_DEFAULTTONEAREST = 2;
    public const uint WM_HOTKEY = 0x312, MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint hwnd, int id);
    public const int VK_LBUTTON = 0x01, VK_MENU = 0x12, IDC_ARROW = 32512, IDC_SIZEALL = 32646;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    public delegate bool MonitorEnumProc(nint mon, nint hdc, nint rect, nint data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] public static extern bool GetMonitorInfoEx(nint mon, ref MONITORINFOEX mi);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint mon, int type, out uint dpiX, out uint dpiY);
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int vk);

    public static int LoWord(nint v) => unchecked((short)((long)v & 0xFFFF));
    public static int HiWord(nint v) => unchecked((short)(((long)v >> 16) & 0xFFFF));
}
