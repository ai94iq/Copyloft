using Copyloft.Core.Security;
using Copyloft.Presentation.Hosting;
using Copyloft.Presentation.Services;

namespace Copyloft.Presentation.Tests;

public sealed class LockPasswordServiceTests
{
    private readonly FakeSettingsStore _file = new();
    private readonly FakeSecureSettings _secure = new();
    private readonly ManualTime _time = new();
    private readonly SettingsService _service;

    public LockPasswordServiceTests() => _service = new SettingsService(_file, new AppSettings());

    [Fact]
    public void Hash_saved_in_db()
    {
        New().Set("1234");

        Assert.NotNull(_secure.Get("LockPasswordHash"));
        Assert.Null(_file.Saved);
        Assert.True(New().Verify("1234"));
    }

    [Fact]
    public void Legacy_hash_moves_to_db()
    {
        _service.Update(_service.Current with { LockPasswordHash = PasswordHash.Create("1234") });

        var passwords = New();

        Assert.True(passwords.Verify("1234"));
        Assert.Null(_service.Current.LockPasswordHash);
        Assert.True(_file.Saved is { LockPasswordHash: null });
    }

    // Someone who can write settings.json must not be able to replace the password.
    [Fact]
    public void Planted_hash_is_ignored()
    {
        New().Set("1234");
        _service.Update(_service.Current with { LockPasswordHash = PasswordHash.Create("evil") });

        var passwords = New();

        Assert.False(passwords.Verify("evil"));
        Assert.True(passwords.Verify("1234"));
    }

    // Deleting the hash from settings.json used to remove the password.
    [Fact]
    public void Cleared_file_keeps_password()
    {
        New().Set("1234");
        _service.Update(new AppSettings());

        Assert.True(New().IsSet);
    }

    [Fact]
    public void First_tries_are_free()
    {
        var passwords = New();
        passwords.Set("1234");

        for (var i = 1; i < LockPasswordService.FreeTries; i++) Assert.False(passwords.Verify("wrong"));

        Assert.Equal(TimeSpan.Zero, passwords.RetryAfter);
        Assert.True(passwords.Verify("1234"));
    }

    [Fact]
    public void Throttle_blocks_right_password()
    {
        var passwords = New();
        passwords.Set("1234");
        for (var i = 0; i < LockPasswordService.FreeTries; i++) passwords.Verify("wrong");

        Assert.Equal(LockPasswordService.FirstWait, passwords.RetryAfter);
        Assert.False(passwords.Verify("1234"));

        _time.Advance(LockPasswordService.FirstWait);
        Assert.True(passwords.Verify("1234"));
        Assert.Equal(TimeSpan.Zero, passwords.RetryAfter);
    }

    [Fact]
    public void Wait_doubles_up_to_cap()
    {
        var passwords = New();
        passwords.Set("1234");
        for (var i = 0; i < LockPasswordService.FreeTries; i++) passwords.Verify("wrong");

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            waits.Add(passwords.RetryAfter);
            _time.Advance(passwords.RetryAfter);
            passwords.Verify("wrong");
        }

        Assert.Equal(
            [30, 60, 120, 240, 480, 900, 900, 900],
            waits.Select(wait => (int)wait.TotalSeconds));
    }

    // Restarting the app is not a way around the wait.
    [Fact]
    public void Wait_survives_restart()
    {
        var passwords = New();
        passwords.Set("1234");
        for (var i = 0; i < LockPasswordService.FreeTries; i++) passwords.Verify("wrong");

        var restarted = New();

        Assert.Equal(LockPasswordService.FirstWait, restarted.RetryAfter);
        Assert.False(restarted.Verify("1234"));
    }

    [Fact]
    public void Clock_rollback_caps_wait()
    {
        var passwords = New();
        passwords.Set("1234");
        for (var i = 0; i < LockPasswordService.FreeTries; i++) passwords.Verify("wrong");

        _time.Advance(-TimeSpan.FromDays(30));

        Assert.Equal(LockPasswordService.MaxWait, passwords.RetryAfter);
    }

    private LockPasswordService New() => new(_service, _secure, _time);
}
