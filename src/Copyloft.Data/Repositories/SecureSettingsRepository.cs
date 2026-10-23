using Copyloft.Core.Abstractions;

namespace Copyloft.Data.Repositories;

// Key/value rows in the Setting table. Synchronous: callers read a handful of values once and
// write rarely (a lock, an unlock, a password change).
public sealed class SecureSettingsRepository(SqliteConnectionFactory factory) : ISecureSettings
{
    public string? Get(string key)
    {
        using var connection = factory.Open();
        return connection.ExecuteScalar<string?>("SELECT Value FROM Setting WHERE Key = @key;", new { key });
    }

    public void Set(string key, string? value)
    {
        using var connection = factory.Open();
        connection.Execute(
            value is null
                ? "DELETE FROM Setting WHERE Key = @key;"
                : "INSERT INTO Setting (Key, Value) VALUES (@key, @value) ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;",
            new { key, value });
    }
}
