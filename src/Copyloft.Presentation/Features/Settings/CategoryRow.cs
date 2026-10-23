namespace Copyloft.Presentation.Features.Settings;

// One row of the settings Categories list: name plus the lock actions that apply right now.
public sealed class CategoryRow(long id, string name, bool isLocked, bool isUnlocked)
{
    public long Id { get; } = id;

    public string Name { get; } = name;

    public bool IsLocked { get; } = isLocked;

    // Not locked yet: offer the lock.
    public bool ShowLock { get; } = !isLocked;

    // Locked and hidden: unlock needs the password.
    public bool ShowUnlock { get; } = isLocked && !isUnlocked;

    // Locked but unlocked for this session: hide it again at once.
    public bool ShowLockNow { get; } = isLocked && isUnlocked;
}
