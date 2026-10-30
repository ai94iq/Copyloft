using System.Globalization;
using System.Text.Json;
using Copyloft.Core.Abstractions;

namespace Copyloft.Presentation.Services;

// Remembers which locked categories are unlocked, in the encrypted database, so the list can
// survive a restart unless a lock-on event clears it. A crash or power loss skips those events,
// so startup applies them again: with lock-on-exit nothing survives a restart, and with
// lock-on-shutdown nothing survives a reboot. Unlocks an older version left in settings.json
// are dropped, not trusted.
public sealed class CategoryLockService : ICategoryLockService
{
    private const string UnlockedKey = "UnlockedCategories";
    private const string BootKey = "UnlockedBoot";

    // Boot time is derived from the uptime counter, which drifts a little and, off Windows,
    // pauses during sleep; a wider gap than this counts as a reboot and relocks.
    private static readonly TimeSpan SameBoot = TimeSpan.FromMinutes(5);

    private readonly ISecureSettings _store;
    private readonly IDataChangeNotifier _changes;
    private readonly DateTimeOffset _boot;
    private readonly HashSet<long> _unlocked;

    public CategoryLockService(SettingsService settings, IDataChangeNotifier changes, ISecureSettings store, TimeProvider time)
        : this(settings, changes, store, time.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64))
    {
    }

    internal CategoryLockService(SettingsService settings, IDataChangeNotifier changes, ISecureSettings store, DateTimeOffset boot)
    {
        _store = store;
        _changes = changes;
        _boot = boot;

        if (settings.Current.UnlockedCategories.Length > 0)
            settings.Update(settings.Current with { UnlockedCategories = [] });

        var saved = ReadUnlocked(store.Get(UnlockedKey));
        var sameBoot = DateTimeOffset.TryParse(store.Get(BootKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var savedBoot) &&
                       (savedBoot - boot).Duration() < SameBoot;
        var keep = !settings.Current.LockOnExit && (!settings.Current.LockOnShutdown || sameBoot);
        _unlocked = keep ? [.. saved] : [];
        if (saved.Length > 0 && !keep) Persist();
    }

    public IReadOnlyCollection<long> UnlockedCategoryIds => _unlocked;

    public void Unlock(long categoryId)
    {
        if (!_unlocked.Add(categoryId)) return;

        Persist();
        _changes.Notify(new DataChanged("lock"));
    }

    public void Lock(long categoryId)
    {
        if (!_unlocked.Remove(categoryId)) return;

        Persist();
        _changes.Notify(new DataChanged("lock"));
    }

    public void Reset()
    {
        if (_unlocked.Count == 0) return;

        _unlocked.Clear();
        Persist();
        _changes.Notify(new DataChanged("lock"));
    }

    // Anything unreadable counts as nothing unlocked.
    private static long[] ReadUnlocked(string? json)
    {
        try
        {
            return json is null ? [] : JsonSerializer.Deserialize<long[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Persist()
    {
        _store.Set(UnlockedKey, _unlocked.Count == 0 ? null : JsonSerializer.Serialize(_unlocked.Order().ToArray()));
        _store.Set(BootKey, _unlocked.Count == 0 ? null : _boot.ToString("O", CultureInfo.InvariantCulture));
    }
}
