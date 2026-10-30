using System.Text;

namespace Copyloft.Data.Tests;

public sealed class DatabaseEncryptionTests
{
    [Fact]
    public void An_encrypted_database_round_trips_and_hides_its_header()
    {
        using var db = new TempDatabase(TempDatabase.Key);

        using (var connection = db.Factory.Open())
        {
            connection.Execute("INSERT INTO Clip (Text, TextHash, CreatedAtUtc) VALUES ('secret', 'h1', '2026-01-01T00:00:00.0000000+00:00');");
        }

        using (var connection = db.Factory.Open())
        {
            Assert.Equal("secret", connection.ExecuteScalar<string>("SELECT Text FROM Clip;"));
        }

        SqliteConnection.ClearAllPools();   // release pooled handles before reading the raw file
        Assert.False(IsPlaintext(db.Options.DatabasePath));
    }

    [Fact]
    public void A_wrong_key_cannot_read_the_database()
    {
        using var db = new TempDatabase(TempDatabase.Key);
        using (var connection = db.Factory.Open())
            connection.Execute("INSERT INTO Clip (Text, TextHash, CreatedAtUtc) VALUES ('x', 'h', '2026-01-01T00:00:00.0000000+00:00');");

        var wrong = db.Options with { EncryptionKey = new string('F', 64) };

        Assert.ThrowsAny<Exception>(() =>
        {
            using var connection = new SqliteConnectionFactory(wrong).Open();
            connection.ExecuteScalar<string>("SELECT Text FROM Clip;");
        });
    }

    [Fact]
    public void An_existing_plaintext_database_is_encrypted_and_keeps_its_rows()
    {
        using var plain = new TempDatabase();
        using (var connection = plain.Factory.Open())
            connection.Execute("INSERT INTO Clip (Text, TextHash, CreatedAtUtc) VALUES ('kept', 'h1', '2026-01-01T00:00:00.0000000+00:00');");
        SqliteConnection.ClearAllPools();
        Assert.True(IsPlaintext(plain.Options.DatabasePath));

        var encryptedOptions = plain.Options with { EncryptionKey = TempDatabase.Key };
        var factory = new SqliteConnectionFactory(encryptedOptions);
        new DatabaseInitializer(factory, encryptedOptions, NullLogger<DatabaseInitializer>.Instance).BackupAndMigrate();

        using (var connection = factory.Open())
            Assert.Equal("kept", connection.ExecuteScalar<string>("SELECT Text FROM Clip;"));
        SqliteConnection.ClearAllPools();
        Assert.False(IsPlaintext(encryptedOptions.DatabasePath));
    }

    [Fact]
    public void Encrypting_removes_plaintext_backups()
    {
        using var plain = new TempDatabase();
        Directory.CreateDirectory(plain.Options.BackupDirectory);
        var staleBackup = Path.Combine(plain.Options.BackupDirectory, "data-20260101-000000-daily.db");
        File.WriteAllText(staleBackup, "plaintext copy");

        var encryptedOptions = plain.Options with { EncryptionKey = TempDatabase.Key };
        new DatabaseInitializer(new SqliteConnectionFactory(encryptedOptions), encryptedOptions, NullLogger<DatabaseInitializer>.Instance).BackupAndMigrate();

        Assert.False(File.Exists(staleBackup));
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(encryptedOptions.BackupDirectory, "data-*.db"))
            Assert.False(IsPlaintext(file));
    }

    [Fact]
    public void No_readable_clip_text_lands_in_the_database_files()
    {
        using var db = new TempDatabase(TempDatabase.Key);
        const string sentinel = "COPYLOFT-SENTINEL-9F3A7C1D";

        using (var connection = db.Factory.Open())
        {
            connection.Execute(
                """
                INSERT INTO Clip (Text, TextHash, CreatedAtUtc)
                VALUES (@text, 'hash', '2026-01-01T00:00:00.0000000+00:00');
                """,
                new { text = sentinel + new string('x', 2000) });
            connection.Execute("INSERT INTO Category (Name) VALUES (@name);", new { name = sentinel });
            connection.Execute("PRAGMA wal_checkpoint(FULL);");
        }

        db.Initializer().BackupAndMigrate();   // also writes a fresh encrypted backup
        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(db.Options.DatabasePath)!;
        var files = Directory.GetFiles(directory, "test.db*")
            .Concat(Directory.GetFiles(db.Options.BackupDirectory, "data-*.db"))
            .ToList();

        Assert.Contains(db.Options.DatabasePath, files);
        Assert.True(files.Count >= 2, "the database and a fresh backup should exist");
        foreach (var file in files)
            Assert.False(ContainsText(file, sentinel), file);
    }

    // A run that died before the sweep, or a backup copied back by hand.
    [Fact]
    public void Plaintext_backup_swept()
    {
        using var plain = new TempDatabase();
        using var db = new TempDatabase(TempDatabase.Key);
        SqliteConnection.ClearAllPools();
        var stray = Path.Combine(db.Options.BackupDirectory, "data-20260101-000000-daily.db");
        File.Copy(plain.Options.DatabasePath, stray);

        db.Initializer().BackupAndMigrate();

        Assert.False(File.Exists(stray));
    }

    // A crash after the plaintext file stepped aside, before the encrypted one moved in.
    [Fact]
    public void Interrupted_encrypt_recovers()
    {
        using var plain = new TempDatabase();
        using (var connection = plain.Factory.Open())
            connection.Execute("INSERT INTO Clip (Text, TextHash, CreatedAtUtc) VALUES ('kept', 'h1', '2026-01-01T00:00:00.0000000+00:00');");
        SqliteConnection.ClearAllPools();
        File.Move(plain.Options.DatabasePath, plain.Options.DatabasePath + ".plaintext");

        var options = plain.Options with { EncryptionKey = TempDatabase.Key };
        var factory = new SqliteConnectionFactory(options);
        new DatabaseInitializer(factory, options, NullLogger<DatabaseInitializer>.Instance).BackupAndMigrate();

        using (var connection = factory.Open())
            Assert.Equal("kept", connection.ExecuteScalar<string>("SELECT Text FROM Clip;"));
        SqliteConnection.ClearAllPools();
        Assert.False(IsPlaintext(options.DatabasePath));
        Assert.False(File.Exists(options.DatabasePath + ".plaintext"));
    }

    [Fact]
    public void Leftover_plaintext_removed()
    {
        using var db = new TempDatabase(TempDatabase.Key);
        var leftover = db.Options.DatabasePath + ".plaintext";
        File.WriteAllText(leftover, "old history");

        db.Initializer().BackupAndMigrate();

        Assert.False(File.Exists(leftover));
    }

    private static bool ContainsText(string path, string text) =>
        File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static bool IsPlaintext(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[16];
        return stream.Read(header, 0, header.Length) == header.Length &&
               header.SequenceEqual("SQLite format 3\0"u8.ToArray());
    }
}
