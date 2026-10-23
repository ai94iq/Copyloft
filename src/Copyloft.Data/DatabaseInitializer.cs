namespace Copyloft.Data;

public sealed class DatabaseInitializer(
    SqliteConnectionFactory factory, DataOptions options, ILogger<DatabaseInitializer> log)
{
    private const string Prefix = "Migrations.";
    private const int BackupsToKeep = 14;

    // Runs on a background thread at startup: encrypt an existing plaintext file, backup (if
    // due), then pending migrations.
    public void BackupAndMigrate()
    {
        Directory.CreateDirectory(options.BackupDirectory);
        if (options.EncryptionKey is not null) FinishInterruptedEncryption();
        if (options.EncryptionKey is not null && IsPlaintext(options.DatabasePath))
            EncryptExistingDatabase();
        if (options.EncryptionKey is not null) RemovePlaintextBackups();

        var migrations = LoadMigrations();
        using var connection = factory.Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        var current = connection.ExecuteScalar<long>("PRAGMA user_version;");
        var pending = migrations.Where(m => m.Version > current).ToList();

        if (current > 0 && (pending.Count > 0 || BackupIsDue()))
            Backup(connection, pending.Count > 0 ? "pre-migration" : "daily");

        foreach (var migration in pending)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(migration.Sql, transaction: transaction);
            // PRAGMA can't take parameters; the version comes from our own file name.
            connection.Execute(
                string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};"),
                transaction: transaction);
            transaction.Commit();
            log.LogInformation("Applied migration {Version} ({Name})", migration.Version, migration.Name);
        }
    }

    // SQLCipher writes a random header, so the SQLite magic string means the file is plaintext.
    private static bool IsPlaintext(string path)
    {
        if (!File.Exists(path)) return false;

        // Read-share so a pooled connection in this or another process does not block the check.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[16];
        return stream.Read(header, 0, header.Length) == header.Length &&
               header.SequenceEqual("SQLite format 3\0"u8.ToArray());
    }

    // Moves an existing plaintext database into an encrypted copy with sqlcipher_export, then
    // replaces the original. The original stays untouched unless the export succeeds.
    private void EncryptExistingDatabase()
    {
        var database = options.DatabasePath;
        var target = database + ".encrypted";
        if (File.Exists(target)) File.Delete(target);

        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            var version = connection.ExecuteScalar<long>("PRAGMA user_version;");
            // Parameters, so the key never sits in SQL text that an error message could log.
            connection.Execute(
                "ATTACH DATABASE @target AS encrypted KEY @key;", new { target, key = options.EncryptionKey });
            connection.Execute("SELECT sqlcipher_export('encrypted');");
            // sqlcipher_export copies schema and data but not user_version.
            connection.Execute(string.Create(CultureInfo.InvariantCulture,
                $"PRAGMA encrypted.user_version = {version};"));
            connection.Execute("DETACH DATABASE encrypted;");
        }

        // The plaintext file steps aside before the encrypted one takes its place, so it can be
        // zeroed instead of left in freed blocks; FinishInterruptedEncryption covers a crash
        // between the two moves.
        SqliteConnection.ClearAllPools();
        File.Move(database, PlaintextLeftover, overwrite: true);
        File.Move(target, database);
        Shred(PlaintextLeftover);
        Shred(database + "-wal");   // plaintext sidecars must not survive
        Shred(database + "-shm");

        // Backups from before encryption would keep readable copies of the history around.
        foreach (var backup in BackupFiles().ToList()) Shred(backup.FullName);

        log.LogInformation("Encrypted the existing database");
    }

    private string PlaintextLeftover => options.DatabasePath + ".plaintext";

    // A crash between the two moves leaves no database but the plaintext one aside: put it back
    // so encryption runs again. With the encrypted database in place, the leftover only needs zeroing.
    private void FinishInterruptedEncryption()
    {
        if (!File.Exists(PlaintextLeftover)) return;

        if (File.Exists(options.DatabasePath)) Shred(PlaintextLeftover);
        else File.Move(PlaintextLeftover, options.DatabasePath);
    }

    // Every start, not only right after encrypting: an interrupted run, or a backup copied back
    // by hand, must not keep plaintext history in the backups folder.
    private void RemovePlaintextBackups()
    {
        foreach (var backup in BackupFiles().Where(file => IsPlaintext(file.FullName)).ToList())
        {
            Shred(backup.FullName);
            log.LogInformation("Removed a plaintext backup ({Name})", backup.Name);
        }
    }

    // Best effort: zeroes a file before deleting it, so the plaintext is not simply left in the
    // freed blocks. SSD wear levelling and copy-on-write file systems can still keep old copies.
    // A file that cannot be removed is logged and left, never a failed start.
    private void Shred(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[81920];
                for (var left = stream.Length; left > 0; left -= zeros.Length)
                    stream.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
                stream.Flush(flushToDisk: true);
            }
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not remove {Name}; it may still hold plaintext history", Path.GetFileName(path));
        }
    }

    private void Backup(SqliteConnection connection, string reason)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var file = Path.Combine(options.BackupDirectory, $"data-{stamp}-{reason}.db");
        connection.Execute("VACUUM INTO @file;", new { file });
        log.LogInformation("Database backup written ({Reason})", reason);

        foreach (var old in BackupFiles().OrderByDescending(f => f.Name).Skip(BackupsToKeep))
            old.Delete();
    }

    private bool BackupIsDue()
    {
        var newest = BackupFiles().MaxBy(f => f.LastWriteTimeUtc);
        return newest is null || DateTime.UtcNow - newest.LastWriteTimeUtc > TimeSpan.FromDays(1);
    }

    private IEnumerable<FileInfo> BackupFiles() =>
        new DirectoryInfo(options.BackupDirectory).EnumerateFiles("data-*.db");

    private static List<Migration> LoadMigrations()
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var name = n[Prefix.Length..];                 // e.g. "0001_initial.sql"
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return new Migration(int.Parse(name[..4], CultureInfo.InvariantCulture), name, reader.ReadToEnd());
            })
            .OrderBy(m => m.Version)
            .ToList();
        CheckVersions([.. migrations.Select(m => (m.Version, m.Name))]);
        return migrations;
    }

    // Two files with one version would apply in name order and only one would be recorded; a
    // gap would hide a missing file. Either is a packaging mistake, so startup stops on it.
    internal static void CheckVersions(IReadOnlyList<(int Version, string Name)> migrations)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException(
                    $"Migration {migrations[i].Name} has version {migrations[i].Version}; expected {i + 1}");
        }
    }

    private sealed record Migration(int Version, string Name, string Sql);
}
