using Copyloft.Presentation.Hosting;
using Copyloft.Presentation.Platform;
using Copyloft.Presentation.Services;
using Copyloft.Core.Abstractions;
using NSubstitute;

namespace Copyloft.Presentation.Tests;

public sealed class SessionCleanupTests
{
    private readonly ISessionEnd _session = Substitute.For<ISessionEnd>();
    private readonly ICategoryLockService _locks = Substitute.For<ICategoryLockService>();

    // Screen lock, user switch and sleep relock whatever the lock-on settings say.
    [Fact]
    public void Stepping_away_locks_every_category_again()
    {
        New(new AppSettings { LockOnExit = false, LockOnShutdown = false, LockOnMinimize = false });

        _session.Locking += Raise.Event();

        _locks.Received(1).Reset();
    }

    [Fact]
    public void Session_end_relocks_only_with_lock_on_shutdown()
    {
        New(new AppSettings { LockOnShutdown = false });

        _session.Ending += Raise.Event();

        _locks.DidNotReceive().Reset();
    }

    private SessionCleanup New(AppSettings settings) =>
        new(_session, new SettingsService(new FakeSettingsStore(), settings), Substitute.For<IClipRepository>(), _locks);
}
