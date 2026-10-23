using System.Globalization;
using Copyloft.Core.Security;

namespace Copyloft.Presentation.Services;

// Keeps the password as a salted PBKDF2 hash in the encrypted database; never the plain text.
// A hash left in settings.json by an older version moves there once and leaves the file.
// After FreeTries wrong passwords, each further one makes the next try wait, doubling up to
// MaxWait; the count survives a restart, so restarting the app does not reset it.
public sealed class LockPasswordService : ILockPasswordService
{
    internal const int FreeTries = 5;
    internal static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    private const string HashKey = "LockPasswordHash";
    private const string FailuresKey = "LockFailures";
    private const string RetryAtKey = "LockRetryAt";

    private readonly ISecureSettings _store;
    private readonly TimeProvider _time;
    private string? _hash;
    private int _failures;
    private DateTimeOffset _retryAt;

    public LockPasswordService(SettingsService settings, ISecureSettings store, TimeProvider time)
    {
        _store = store;
        _time = time;

        if (settings.Current.LockPasswordHash is { } legacy)
        {
            // A hash already in the database wins over whatever the file says.
            if (store.Get(HashKey) is null) store.Set(HashKey, legacy);
            settings.Update(settings.Current with { LockPasswordHash = null });
        }

        _hash = store.Get(HashKey);
        _failures = int.TryParse(store.Get(FailuresKey), CultureInfo.InvariantCulture, out var failures) ? failures : 0;
        _retryAt = DateTimeOffset.TryParse(store.Get(RetryAtKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var retryAt)
            ? retryAt
            : DateTimeOffset.MinValue;
    }

    public bool IsSet => _hash is not null;

    // A clock set back must not stretch a wait past MaxWait.
    public TimeSpan RetryAfter
    {
        get
        {
            var left = _retryAt - _time.GetUtcNow();
            return left <= TimeSpan.Zero ? TimeSpan.Zero : left > MaxWait ? MaxWait : left;
        }
    }

    public bool Verify(string password)
    {
        if (RetryAfter > TimeSpan.Zero) return false;

        if (PasswordHash.Verify(password, _hash ?? string.Empty))
        {
            if (_failures > 0) SaveFailures(0, DateTimeOffset.MinValue);
            return true;
        }

        var failures = _failures + 1;
        var retryAt = failures < FreeTries
            ? DateTimeOffset.MinValue
            : _time.GetUtcNow() + Wait(failures - FreeTries);
        SaveFailures(failures, retryAt);
        return false;
    }

    public void Set(string password)
    {
        _hash = PasswordHash.Create(password);
        _store.Set(HashKey, _hash);
        SaveFailures(0, DateTimeOffset.MinValue);
    }

    public void Remove()
    {
        _hash = null;
        _store.Set(HashKey, null);
        SaveFailures(0, DateTimeOffset.MinValue);
    }

    // 30 s, 1 min, 2 min, ... up to MaxWait.
    private static TimeSpan Wait(int extraFailures) =>
        extraFailures >= 5 ? MaxWait : TimeSpan.FromTicks(Math.Min(FirstWait.Ticks << extraFailures, MaxWait.Ticks));

    private void SaveFailures(int failures, DateTimeOffset retryAt)
    {
        _failures = failures;
        _retryAt = retryAt;
        _store.Set(FailuresKey, failures == 0 ? null : failures.ToString(CultureInfo.InvariantCulture));
        _store.Set(RetryAtKey, retryAt == DateTimeOffset.MinValue ? null : retryAt.ToString("O", CultureInfo.InvariantCulture));
    }
}
