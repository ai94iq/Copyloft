using System.Globalization;
using Copyloft.Presentation.Hosting;

namespace Copyloft.Presentation.Tests;

public sealed class SingleInstanceServiceTests
{
    private static string NewName() => "Copyloft.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void FirstClaimSucceeds()
    {
        using var service = new SingleInstanceService(NewName());
        Assert.True(service.TryClaim());
    }

    [Fact]
    public void SecondClaimFails()
    {
        var name = NewName();
        using var first = new SingleInstanceService(name);
        using var second = new SingleInstanceService(name);
        Assert.True(first.TryClaim());
        Assert.False(second.TryClaim());
    }

    // Off Windows Process.SessionId is the POSIX session id, different for a terminal and a
    // launcher; a name built from it let every launch start its own instance.
    [Fact]
    public void Off_windows_the_name_is_per_user_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows scopes the name per session");

        Assert.Equal("Copyloft.SingleInstance." + Environment.UserName, SingleInstanceService.DefaultName);
    }

    // macOS caps a Unix socket path at 104 characters and its per-user TMPDIR is long, so the
    // default CoreFxPipe_<name> path overflowed there; the endpoint is a short hashed path instead.
    [Fact]
    public void Off_windows_the_endpoint_fits_a_unix_socket_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows pipes are not files");
        var name = "Copyloft.SingleInstance." + new string('u', 64);

        var endpoint = SingleInstanceService.Endpoint(name);

        Assert.True(Path.IsPathRooted(endpoint));
        Assert.True(endpoint.Length <= 104, endpoint);
        Assert.NotEqual(endpoint, SingleInstanceService.Endpoint(name + "x"));
    }

    // Off Windows the pipe is a Unix socket another process can simply re-bind, so the claim is
    // the lock file; holding it here stands in for a running instance in another process.
    [Fact]
    public void ClaimFailsWhileAnotherProcessHoldsTheLock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the named pipe itself is the claim on Windows");
        var name = NewName();
        using var held = new FileStream(
            SingleInstanceService.LockPath(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var service = new SingleInstanceService(name);

        Assert.False(service.TryClaim());
    }

    [Fact]
    public async Task SentCommandIsReceived()
    {
        var name = NewName();
        using var service = new SingleInstanceService(name);
        Assert.True(service.TryClaim());

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CommandReceived += (_, command) => received.TrySetResult(command);

        Assert.True(await SingleInstanceService.TrySendAsync(
            name, "--toggle", TestContext.Current.CancellationToken));
        var completed = await Task.WhenAny(
            received.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(received.Task, completed);
        Assert.Equal("--toggle", await received.Task);
    }

    // The app claims the name in Main but listens only once Avalonia has started; a second launch
    // in between must not lose its command.
    [Fact]
    public async Task A_command_sent_before_anyone_listens_is_delivered_on_subscribe()
    {
        var name = NewName();
        using var service = new SingleInstanceService(name);
        Assert.True(service.TryClaim());
        Assert.True(await SingleInstanceService.TrySendAsync(name, "--toggle", TestContext.Current.CancellationToken));
        await Task.Delay(300, TestContext.Current.CancellationToken);   // the server has read it by now

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CommandReceived += (_, command) => received.TrySetResult(command);

        var completed = await Task.WhenAny(
            received.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(received.Task, completed);
        Assert.Equal("--toggle", await received.Task);
    }

    [Fact]
    public async Task SendRetriesUntilServerAppears()
    {
        var name = NewName();
        var send = SingleInstanceService.TrySendAsync(
            name, "--history", TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        using var service = new SingleInstanceService(name);
        Assert.True(service.TryClaim());
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CommandReceived += (_, command) => received.TrySetResult(command);

        Assert.True(await send);
        var completed = await Task.WhenAny(
            received.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(received.Task, completed);
        Assert.Equal("--history", await received.Task);
    }

    [Fact]
    public void ClaimSucceedsAgainAfterDispose()
    {
        var name = NewName();
        var first = new SingleInstanceService(name);
        Assert.True(first.TryClaim());
        first.Dispose();

        using var second = new SingleInstanceService(name);
        Assert.True(second.TryClaim());
    }

    [Fact]
    public async Task SendWithoutServerFails()
    {
        Assert.False(await SingleInstanceService.TrySendAsync(
            NewName(), "--toggle", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DefaultName_is_scoped_to_the_user()
    {
        Assert.StartsWith("Copyloft.SingleInstance.", SingleInstanceService.DefaultName, StringComparison.Ordinal);
        Assert.Contains(Environment.UserName, SingleInstanceService.DefaultName, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultName_is_scoped_to_the_session()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows sessions scope the name");
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        Assert.EndsWith(
            "." + session.ToString(CultureInfo.InvariantCulture),
            SingleInstanceService.DefaultName,
            StringComparison.Ordinal);
    }

    // Only this user's processes may reach the pipe, but even they get no unbounded buffer.
    [Theory]
    [InlineData("--toggle\r\n", "--toggle")]
    [InlineData("--history", "--history")]
    public async Task Command_is_read(string sent, string expected)
    {
        var command = await SingleInstanceService.ReadCommandAsync(new StringReader(sent), TestContext.Current.CancellationToken);

        Assert.Equal(expected, command);
    }

    [Fact]
    public async Task Long_command_dropped()
    {
        var sent = new string('x', SingleInstanceService.MaxCommandLength + 1) + "\n";

        Assert.Null(await SingleInstanceService.ReadCommandAsync(new StringReader(sent), TestContext.Current.CancellationToken));
    }

    // Without XDG_RUNTIME_DIR, a shared /tmp would let another user squat the lock.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Linux_lock_not_in_tmp()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is null, "Linux without XDG_RUNTIME_DIR");

        var directory = Path.GetDirectoryName(SingleInstanceService.LockPath(NewName()))!;

        Assert.NotEqual(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), directory);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
    }
}
