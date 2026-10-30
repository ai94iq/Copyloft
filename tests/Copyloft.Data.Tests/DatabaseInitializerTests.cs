namespace Copyloft.Data.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public void Migrations_are_applied()
    {
        using var db = new TempDatabase();
        using var connection = db.Factory.Open();
        Assert.True(connection.ExecuteScalar<long>("PRAGMA user_version;") >= 1);
    }

    [Fact]
    public void Second_start_writes_a_daily_backup()
    {
        using var db = new TempDatabase();
        db.Initializer().BackupAndMigrate();
        Assert.Single(Directory.GetFiles(db.Options.BackupDirectory, "data-*-daily.db"));
    }

    [Theory]
    [InlineData(1, 1, 2)]     // two files with one version
    [InlineData(1, 3, 4)]     // a missing file
    public void Bad_versions_stop_startup(int first, int second, int third)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseInitializer.CheckVersions([(first, "a.sql"), (second, "b.sql"), (third, "c.sql")]));
    }

    [Fact]
    public void Shipped_versions_pass()
    {
        using var db = new TempDatabase();
        db.Initializer().BackupAndMigrate();
    }
}
