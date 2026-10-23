namespace Copyloft.Presentation.Platform;

// Raised when the OS is ending the session (sign-out or shutdown), and when the user steps away
// from it (screen lock, user switch, sleep).
public interface ISessionEnd
{
    event EventHandler? Ending;

    event EventHandler? Locking;
}
