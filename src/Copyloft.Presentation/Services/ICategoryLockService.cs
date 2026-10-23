namespace Copyloft.Presentation.Services;

// Remembers which locked categories are unlocked; kept in the encrypted database.
public interface ICategoryLockService
{
    IReadOnlyCollection<long> UnlockedCategoryIds { get; }

    void Unlock(long categoryId);

    void Lock(long categoryId);

    // Forgets every unlock; used by the lock-on events and when the password is removed.
    void Reset();
}
