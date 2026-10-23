using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Copyloft.Presentation.Hosting;

// One instance per name. The first process claims the pipe and listens; later processes connect,
// send one command line and exit. NamedPipeServerStream maps to a Unix domain socket off Windows.
public sealed class SingleInstanceService : IDisposable
{
    private readonly string _name;
    private readonly string _prefix;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _server;
    private FileStream? _lock;
    private bool _disposed;

    // The prefix names the socket file off Windows; ClipShelf, the app's name up to 0.4.1, used "clipshelf-".
    public SingleInstanceService(string name, string endpointPrefix = DefaultPrefix)
    {
        _name = name;
        _prefix = endpointPrefix;
    }

    internal const string DefaultPrefix = "copyloft-";

    // Scoped per user and per session: a machine-wide name would let one user's instance block
    // another user's launch, and a per-user-only name would merge two sessions of the same user
    // (RDP), where the old mutex kept them apart. Off Windows the session id is the POSIX one
    // (getsid), which differs between a terminal and a launcher, so only the user scopes the name.
    public static string DefaultName { get; } =
        "Copyloft.SingleInstance." + Environment.UserName +
        (OperatingSystem.IsWindows()
            ? "." + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture)
            : "");

    private readonly Lock _gate = new();
    private readonly Queue<string> _undelivered = new();
    private EventHandler<string>? _commandReceived;

    // Raised on a worker thread. Commands that arrive before anyone listens (the app claims the
    // name in Main but subscribes once Avalonia has started) are kept and delivered on subscribe.
    public event EventHandler<string>? CommandReceived
    {
        add
        {
            string[] pending;
            lock (_gate)
            {
                _commandReceived += value;
                pending = [.. _undelivered];
                _undelivered.Clear();
            }
            foreach (var command in pending) value?.Invoke(this, command);
        }
        remove
        {
            lock (_gate) _commandReceived -= value;
        }
    }

    // Off Windows the pipe is a Unix domain socket that another process can re-bind, so an
    // exclusive lock file (flock, released by the kernel when the process dies) is the claim.
    internal static string LockPath(string name, string prefix = DefaultPrefix) => Endpoint(name, prefix) + ".lock";

    // The pipe's address. Off Windows it is a socket file, and macOS caps those paths at 104
    // characters while its per-user TMPDIR is long, so the file gets a short name hashed from the
    // full one, in the per-user runtime directory where there is one.
    internal static string Endpoint(string name, string prefix = DefaultPrefix)
    {
        if (OperatingSystem.IsWindows()) return name;
        var directory = SocketDirectory(prefix);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16];
        return Path.Combine(directory, prefix + hash.ToLowerInvariant());
    }

    // XDG_RUNTIME_DIR is private to the user. Without it Linux falls back to a shared /tmp, where
    // another user could create the lock first and stop every launch, so Copyloft uses a folder
    // only this user can write. macOS's temp folder is already per user. ClipShelf put its socket
    // in the temp folder, and the probe for it must look where it did.
    private static string SocketDirectory(string prefix)
    {
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime) return runtime;
        if (OperatingSystem.IsMacOS() || prefix != DefaultPrefix) return Path.GetTempPath();

        var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "copyloft-run");
        if (!OperatingSystem.IsWindows())
            Directory.CreateDirectory(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return own;
    }

    // A lock file someone else owns: access is denied rather than the lock being held. For the
    // ClipShelf probe that means it is not this user's ClipShelf.
    internal bool ClaimDenied { get; private set; }

    // False when another instance already owns the name.
    public bool TryClaim()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                _lock = new FileStream(LockPath(_name, _prefix), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _server = new NamedPipeServerStream(
                Endpoint(_name, _prefix), PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the lock file: on Windows an existing pipe can also answer access denied.
            ClaimDenied = _lock is null && !OperatingSystem.IsWindows() && ex is UnauthorizedAccessException;
            _lock?.Dispose();
            _lock = null;
            return false;
        }

        _ = Task.Run(AcceptLoopAsync);
        return true;
    }

    // Connects to the running instance and sends one command; retries briefly so a first instance
    // that is still starting is found. False when nothing answers.
    public static async Task<bool> TrySendAsync(
        string name, string command, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", Endpoint(name), PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(50, cancellationToken);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync(command);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or UnauthorizedAccessException
                    or System.Net.Sockets.SocketException)
            {
                // The server is not there yet; wait and try again.
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _server?.Dispose();
        _lock?.Dispose();
        _cts.Dispose();
    }

    private void Deliver(string command)
    {
        EventHandler<string>? handlers;
        lock (_gate)
        {
            handlers = _commandReceived;
            // Startup subscribes within seconds; a few commands cover that, more are dropped.
            if (handlers is null && _undelivered.Count < MaxUndelivered) _undelivered.Enqueue(command);
        }
        handlers?.Invoke(this, command);
    }

    // Commands are short flags; a longer line is dropped rather than buffered without limit.
    internal const int MaxCommandLength = 256;

    private const int MaxUndelivered = 8;

    internal static async Task<string?> ReadCommandAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[MaxCommandLength + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (read == 0 || buffer[length] == '\n') break;
            length++;
        }

        if (length > MaxCommandLength) return null;
        return new string(buffer, 0, length).TrimEnd('\r');
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _server!.WaitForConnectionAsync(_cts.Token);
                using (var reader = new StreamReader(
                    _server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                {
                    var command = await ReadCommandAsync(reader, _cts.Token);
                    if (!string.IsNullOrEmpty(command))
                        Deliver(command);
                }

                _server.Disconnect();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that disconnected early; reset the pipe and keep listening, with a pause
                // so a pipe that keeps failing cannot spin this loop.
                try
                {
                    if (_server!.IsConnected) _server.Disconnect();
                    await Task.Delay(50, _cts.Token);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
                {
                    if (_cts.IsCancellationRequested) return;
                }
            }
        }
    }
}
