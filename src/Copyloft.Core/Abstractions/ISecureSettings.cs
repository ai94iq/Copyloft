namespace Copyloft.Core.Abstractions;

// Small values kept in the encrypted database instead of settings.json, for state that must not
// be readable or editable without the database key.
public interface ISecureSettings
{
    string? Get(string key);

    // A null value removes the key.
    void Set(string key, string? value);
}
