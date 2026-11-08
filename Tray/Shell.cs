using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NightLight;

internal static class Shell
{
    internal const uint Add = 0, Modify = 1, Delete = 2, SetFocus = 3, SetVersion = 4;
    internal const uint IconFlags = 0x87; // Message, icon, tip, and standard tooltip.
    internal const int AppMessage = 0x8000;
    internal const int ContextMenu = 0x007b, LeftButtonUp = 0x0202, MouseWheel = 0x020a, KeySelect = 0x0401;
    internal const int SettingChange = 0x001a, SysColorChange = 0x0015, ThemeChanged = 0x031a;
    internal const int LowLevelMouse = 14;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct IconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Callback;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconIdentifier
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct ScreenPoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
    {
        internal bool Contains(ScreenPoint point) =>
            point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseEvent
    {
        internal ScreenPoint Point;
        internal uint Data, Flags, Time;
        internal nuint ExtraInfo;
    }

    internal delegate nint MouseCallback(int code, nint message, nint data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool NotifyIcon(uint action, ref IconData data);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int Shell_NotifyIconGetRect(in IconIdentifier icon, out Bounds bounds);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static extern nint InstallHook(int id, MouseCallback callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    internal static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    internal static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(ScreenPoint point);

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("advapi32.dll")]
    internal static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree,
        uint filter, SafeWaitHandle signal, bool asynchronous);
}
