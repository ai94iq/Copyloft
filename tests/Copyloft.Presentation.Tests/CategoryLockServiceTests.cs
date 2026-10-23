using Copyloft.Presentation.Hosting;
using Copyloft.Presentation.Services;
using Copyloft.Core.Abstractions;
using NSubstitute;

namespace Copyloft.Presentation.Tests;

public sealed class CategoryLockServiceTests
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeSettingsStore _file = new();
    private readonly FakeSecureSettings _secure = new();
    private readonly SettingsService _service;

    // Lock-on-exit off, so unlocks may survive a restart within one boot.
    public CategoryLockServiceTests() =>
        _service = new SettingsService(_file, new AppSettings { LockOnExit = false });

    [Fact]
    public void Unlock_saved_in_db()
    {
        var locks = New();

        locks.Unlock(7);

        Assert.Equal("[7]", _secure.Get("UnlockedCategories"));
        Assert.Null(_file.Saved);
    }

    [Fact]
    public void Restart_keeps_unlocks()
    {
        New().Unlock(7);

        var locks = New(Boot + TimeSpan.FromMinutes(1));

        Assert.Contains(7, locks.UnlockedCategoryIds);
    }

    // A crash skips the exit relock; the next start makes up for it.
    [Fact]
    public void Restart_relocks()
    {
        New().Unlock(7);
        _service.Update(_service.Current with { LockOnExit = true });

        var locks = New();

        Assert.Empty(locks.UnlockedCategoryIds);
        Assert.Null(_secure.Get("UnlockedCategories"));
    }

    // A power loss skips the shutdown relock; a new boot makes up for it.
    [Fact]
    public void Reboot_relocks()
    {
        New().Unlock(7);

        var locks = New(Boot + TimeSpan.FromHours(3));

        Assert.Empty(locks.UnlockedCategoryIds);
    }

    [Fact]
    public void Reboot_keeps_unlocks_if_off()
    {
        _service.Update(_service.Current with { LockOnShutdown = false });
        New().Unlock(7);

        var locks = New(Boot + TimeSpan.FromHours(3));

        Assert.Contains(7, locks.UnlockedCategoryIds);
    }

    // An older version kept the list in settings.json, where anyone could add to it.
    [Fact]
    public void Legacy_unlocks_dropped()
    {
        _service.Update(_service.Current with { UnlockedCategories = [7] });

        var locks = New();

        Assert.Empty(locks.UnlockedCategoryIds);
        Assert.True(_file.Saved is { UnlockedCategories: [] });
    }

    [Fact]
    public void Bad_list_unlocks_nothing()
    {
        _secure.Set("UnlockedCategories", "not json");

        Assert.Empty(New().UnlockedCategoryIds);
    }

    [Fact]
    public void Reset_clears_and_saves_the_empty_list()
    {
        var locks = New();
        locks.Unlock(7);

        locks.Reset();

        Assert.Empty(locks.UnlockedCategoryIds);
        Assert.Null(_secure.Get("UnlockedCategories"));
    }

    [Fact]
    public void Locking_one_category_keeps_the_others()
    {
        var locks = New();
        locks.Unlock(7);
        locks.Unlock(8);

        locks.Lock(7);

        Assert.Equal("[8]", _secure.Get("UnlockedCategories"));
    }

    private CategoryLockService New(DateTimeOffset? boot = null) =>
        new(_service, Substitute.For<IDataChangeNotifier>(), _secure, boot ?? Boot);
}
