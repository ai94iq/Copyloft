namespace Copyloft.Presentation.Services;

// The one password that guards exports and locked categories.
public interface ILockPasswordService
{
    bool IsSet { get; }

    // How long until the next try is checked, after too many wrong ones; zero when it can be.
    TimeSpan RetryAfter { get; }

    // False for a wrong password, and for any password while RetryAfter is above zero.
    bool Verify(string password);

    void Set(string password);

    void Remove();
}
