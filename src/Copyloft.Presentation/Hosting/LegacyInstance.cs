using System.Diagnostics;

namespace Copyloft.Presentation.Hosting;

// Whether ClipShelf, the app's name up to 0.4.1, is running for this user and session. It holds the
// data folder Copyloft would move over, so Copyloft asks for it to be quit instead of sharing it.
// The check claims ClipShelf's single-instance name for a moment, the way 0.4.x claimed it.
public static class LegacyInstance
{
    // Where ClipShelf kept its data, next to the Copyloft folder; read for its language while it runs.
    public static string Settings { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShelf", "settings.json");

    public static string Name { get; } =
        "ClipShelf.SingleInstance." + Environment.UserName +
        (OperatingSystem.IsWindows()
            ? "." + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture)
            : "");

    public static bool IsRunning(string? name = null)
    {
        using var probe = new SingleInstanceService(name ?? Name, "clipshelf-");
        return !probe.TryClaim() && !probe.ClaimDenied;
    }
}

// Startup stops with this while ClipShelf runs; the startup window asks for ClipShelf to be quit.
public sealed class LegacyInstanceRunningException() : InvalidOperationException("ClipShelf is still running");
