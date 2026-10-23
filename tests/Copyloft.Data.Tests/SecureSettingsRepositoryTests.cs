using Copyloft.Data.Repositories;

namespace Copyloft.Data.Tests;

public sealed class SecureSettingsRepositoryTests
{
    [Fact]
    public void A_value_is_read_back_replaced_and_removed()
    {
        using var db = new TempDatabase(TempDatabase.Key);
        var store = new SecureSettingsRepository(db.Factory);

        Assert.Null(store.Get("LockPasswordHash"));
        store.Set("LockPasswordHash", "first");
        store.Set("LockPasswordHash", "second");
        Assert.Equal("second", new SecureSettingsRepository(db.Factory).Get("LockPasswordHash"));

        store.Set("LockPasswordHash", null);
        Assert.Null(store.Get("LockPasswordHash"));
    }

    // The point of the table: the file on disk does not show the value.
    [Fact]
    public void Values_are_not_readable_in_the_database_file()
    {
        using var db = new TempDatabase(TempDatabase.Key);
        new SecureSettingsRepository(db.Factory).Set("LockPasswordHash", "needle-in-the-file");
        SqliteConnection.ClearAllPools();

        var bytes = File.ReadAllBytes(db.Options.DatabasePath);
        var wal = db.Options.DatabasePath + "-wal";
        if (File.Exists(wal)) bytes = [.. bytes, .. File.ReadAllBytes(wal)];

        Assert.DoesNotContain("needle-in-the-file", System.Text.Encoding.UTF8.GetString(bytes));
    }
}
