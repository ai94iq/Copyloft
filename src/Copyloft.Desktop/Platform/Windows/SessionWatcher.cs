using System.Runtime.Versioning;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Copyloft.Desktop.Platform.Windows;

// A hidden top-level window that listens for the session-end notification, so the history
// can be cleared on sign-out or shutdown before Windows tears the process down, and for screen
// lock, user switch and sleep, so unlocked categories lock again while the user is away.
[SupportedOSPlatform("windows")]
internal sealed class SessionWatcher : ISessionEnd
{
    private const string ClassName = "Copyloft.SessionWindow";

    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    // Held statically so the window class never points at a collected delegate.
    private static readonly WindowProc Proc = HandleMessage;
    private static SessionWatcher? _current;
    private static bool _classRegistered;

    public SessionWatcher()
    {
        _current = this;
        RegisterClassOnce();

        var handle = NativeMethods.CreateWindowEx(
            0, ClassName, ClassName, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Creating the session window failed.");

        // Without it there is no screen-lock notice; sleep still arrives, so this is not fatal.
        NativeMethods.WTSRegisterSessionNotification(handle, NativeMethods.NotifyForThisSession);
    }

    // Raised when Windows is ending the session (sign-out or shutdown).
    public event EventHandler? Ending;

    public event EventHandler? Locking;

    private static void RegisterClassOnce()
    {
        if (_classRegistered) return;

        var windowClass = new NativeMethods.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            LpszClassName = ClassName,
        };

        var atom = NativeMethods.RegisterClassEx(ref windowClass);
        if (atom == 0 && Marshal.GetLastWin32Error() != NativeMethods.ErrorClassAlreadyExists)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Registering the session window class failed.");

        _classRegistered = true;
    }

    // Screen lock (Win+L), a switch to another user or a remote disconnect, and sleep.
    internal static bool IsSteppingAway(uint message, int code) =>
        (message == NativeMethods.MessageSessionChange &&
         code is NativeMethods.SessionLock or NativeMethods.SessionConsoleDisconnect or NativeMethods.SessionRemoteDisconnect) ||
        (message == NativeMethods.MessagePowerBroadcast && code == NativeMethods.PowerSuspend);

    private static IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.MessageEndSession && wParam != IntPtr.Zero)
            _current?.Ending?.Invoke(_current, EventArgs.Empty);
        else if (IsSteppingAway(message, (int)wParam))
            _current?.Locking?.Invoke(_current, EventArgs.Empty);

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }
}
