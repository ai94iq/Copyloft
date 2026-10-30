namespace Copyloft.Presentation.Services;

// Clears the unpinned history when the OS ends the session, if the setting is on, and locks
// unlocked categories again when the session ends or the user steps away (screen lock, user
// switch, sleep), so nobody at the unattended machine finds them open.
public sealed class SessionCleanup
{
    public SessionCleanup(
        ISessionEnd sessionEnd, SettingsService settings, IClipRepository repository, ICategoryLockService locks)
    {
        sessionEnd.Ending += (_, _) =>
        {
            if (settings.Current.LockOnShutdown) locks.Reset();
            if (settings.Current.ClearOnSignOut) repository.ClearUnpinned();
        };
        sessionEnd.Locking += (_, _) => locks.Reset();
    }
}
