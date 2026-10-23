using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Copyloft.Desktop.Platform;

// Sign-out and shutdown reach the app as SIGTERM on Linux and macOS. The host's console lifetime
// also catches SIGTERM and cancels the default exit, so after the cleanup runs this exits the
// process itself. macOS ends a session with a quit Apple event, which Avalonia raises as
// ShutdownRequested; that raises Ending too (the cleanup is safe to run twice).
public sealed class PosixSessionEnd : ISessionEnd, IDisposable
{
    private readonly Action<int> _exit;
    private readonly PosixSignalRegistration? _registration;

    public PosixSessionEnd() : this(Environment.Exit)
    {
        _registration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => OnTerminate());
        if (OperatingSystem.IsMacOS() && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownRequested += (_, _) => Ending?.Invoke(this, EventArgs.Empty);
    }

    internal PosixSessionEnd(Action<int> exit) => _exit = exit;

    public event EventHandler? Ending;

    // Not raised yet: screen lock and sleep would need logind (D-Bus) or NSWorkspace observers.
    public event EventHandler? Locking { add { } remove { } }

    internal void OnTerminate()
    {
        Ending?.Invoke(this, EventArgs.Empty);
        _exit(0);
    }

    public void Dispose() => _registration?.Dispose();
}
