using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;

#pragma warning disable SYSLIB1054 // DllImport is intentional: the app ships self-contained, not Native AOT.

namespace Copyloft.Desktop.Platform.Windows;

// The Win32 surface used for clipboard access, the change listener, the global hotkey and the
// panel window. Win32 BOOL is 4 bytes, so every bool is marshalled explicitly.
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    internal const uint MessageClipboardUpdate = 0x031D;
    internal const uint MessageHotkey = 0x0312;
    internal const uint MessageEndSession = 0x0016;
    internal const uint MessageSettingChange = 0x001A;
    internal const uint MessageThemeChanged = 0x031A;
    internal const uint MessageSessionChange = 0x02B1;
    internal const uint MessagePowerBroadcast = 0x0218;

    // WM_WTSSESSION_CHANGE and WM_POWERBROADCAST codes that mean the user stepped away.
    internal const int SessionConsoleDisconnect = 0x1;
    internal const int SessionRemoteDisconnect = 0x4;
    internal const int SessionLock = 0x7;
    internal const int PowerSuspend = 0x4;
    internal const uint NotifyForThisSession = 0;

    internal const uint ClipboardFormatUnicodeText = 13;
    internal const uint ClipboardFormatDib = 8;
    internal const uint GlobalMemoryMoveable = 0x0002;
    internal const uint GlobalMemoryZeroInit = 0x0040;

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;

    internal const int ErrorClassAlreadyExists = 1410;

    // HKEY_CURRENT_USER (0x80000001 sign-extended) and the registry-notification flags used by
    // the theme watcher.
    internal static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
    internal const uint KeyNotify = 0x0010;
    internal const uint NotifyChangeLastSet = 0x00000004;

    internal const int GwlExStyle = -20;
    internal const int GwlStyle = -16;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExWindowEdge = 0x00000100;
    internal const int WsExClientEdge = 0x00000200;
    internal const int WsCaption = 0x00C00000;
    internal const int WsThickFrame = 0x00040000;
    internal const int WsBorder = 0x00800000;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const byte VirtualKeyControl = 0x11;
    internal const byte VirtualKeyMenu = 0x12;
    internal const byte VirtualKeyV = 0x56;
    internal const uint KeyEventKeyUp = 0x0002;

    // The theme watcher: blocks on (or is signalled by) writes to the Personalize key.
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int RegOpenKeyEx(IntPtr key, string subKey, uint options, uint access, out IntPtr result);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern int RegCloseKey(IntPtr key);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern int RegNotifyChangeKeyValue(
        IntPtr key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint filter,
        IntPtr eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint GlobalSize(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    internal const int DwmImmersiveDarkMode = 20;
    internal const int DwmWindowCornerPreference = 33;
    internal const int DwmWindowCornerRound = 2;
    internal const int DwmWindowBorderColor = 34;
    internal const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    internal const uint EventSystemForeground = 0x0003;
    internal const uint WineventOutOfContext = 0x0000;

    internal delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr window, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowEx(
        uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassEx
    {
        internal uint CbSize;
        internal uint Style;
        internal IntPtr LpfnWndProc;
        internal int CbClsExtra;
        internal int CbWndExtra;
        internal IntPtr HInstance;
        internal IntPtr HIcon;
        internal IntPtr HCursor;
        internal IntPtr HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string LpszClassName;
        internal IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint CbSize;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }
}
