using Copyloft.Desktop.Platform;
using Copyloft.Desktop.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;

namespace Copyloft.Desktop.Tests;

// The keyring is faked; the file rules are what keep an existing history readable.
public sealed class KeyringKeyStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("copyloft-key").FullName;
    private string KeyPath => Path.Combine(_dir, "db.key");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private KeyringKeyStore Store(FakeKeyring keyring) => new(keyring, NullLogger<KeyringKeyStore>.Instance);

    [Fact]
    public void A_new_key_goes_to_the_keyring_and_the_file_only_marks_it()
    {
        var keyring = new FakeKeyring();

        var key = Store(keyring).LoadOrCreate(KeyPath);

        Assert.Equal(64, key.Length);
        Assert.Equal(key, keyring.Secret);
        Assert.Equal(KeyringKeyStore.KeyringMarker, File.ReadAllText(KeyPath));
        Assert.Equal(key, Store(keyring).LoadOrCreate(KeyPath));
    }

    [Fact]
    public void Without_a_keyring_the_key_lands_in_a_file_only_the_user_can_read()
    {
        var keyring = new FakeKeyring { Available = false };

        var key = Store(keyring).LoadOrCreate(KeyPath);

        Assert.Equal(key, File.ReadAllText(KeyPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.Equal(key, Store(new FakeKeyring()).LoadOrCreate(KeyPath));
    }

    [Fact]
    public void A_marked_key_the_keyring_cannot_give_back_is_an_error_not_a_new_key()
    {
        File.WriteAllText(KeyPath, KeyringKeyStore.KeyringMarker);

        Assert.Throws<KeyringUnavailableException>(() => Store(new FakeKeyring { Available = false }).LoadOrCreate(KeyPath));
        Assert.Throws<KeyringUnavailableException>(() => Store(new FakeKeyring()).LoadOrCreate(KeyPath));
        Assert.Equal(KeyringKeyStore.KeyringMarker, File.ReadAllText(KeyPath));
    }

    // A keyring that answers but says no must not end with the key in a plain file.
    [Fact]
    public void Refused_store_throws()
    {
        Assert.Throws<KeyringUnavailableException>(() => Store(new FakeKeyring { Refuses = true }).LoadOrCreate(KeyPath));
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void File_key_moves_to_keyring()
    {
        var key = Store(new FakeKeyring { Available = false }).LoadOrCreate(KeyPath);
        var keyring = new FakeKeyring();

        Assert.Equal(key, Store(keyring).LoadOrCreate(KeyPath));

        Assert.Equal(key, keyring.Secret);
        Assert.Equal(KeyringKeyStore.KeyringMarker, File.ReadAllText(KeyPath));
        Assert.Equal(key, Store(keyring).LoadOrCreate(KeyPath));
    }

    // Some other key already in the keyring: replacing it could orphan what it opens.
    [Fact]
    public void Other_keyring_key_keeps_file()
    {
        var key = Store(new FakeKeyring { Available = false }).LoadOrCreate(KeyPath);
        var keyring = new FakeKeyring { Secret = "other" };

        Assert.Equal(key, Store(keyring).LoadOrCreate(KeyPath));

        Assert.Equal("other", keyring.Secret);
        Assert.Equal(key, File.ReadAllText(KeyPath));
    }

    // The real binding against whatever the machine has: answers either way, never crashes.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void The_libsecret_binding_answers_on_this_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "libsecret");

        var found = new LibSecretKeyring(NullLogger<LibSecretKeyring>.Instance).TryLookup(out var secret);

        Assert.True(found || secret is null);
    }

    private sealed class FakeKeyring : ISecretKeyring
    {
        public bool Available { get; init; } = true;
        public bool Refuses { get; init; }
        public string? Secret { get; set; }

        public bool TryLookup(out string? secret)
        {
            secret = Secret;
            return Available;
        }

        public bool TryStore(string secret)
        {
            if (Available && !Refuses)
                Secret = secret;
            return Available && !Refuses;
        }
    }
}
