using System.Text;

namespace Copyloft.Desktop.Platform.Linux;

// An XDG autostart entry (~/.config/autostart/copyloft.desktop) that starts Copyloft at sign-in.
// Applying every launch also repairs the path after the app moves.
public sealed class AutostartRegistration(
    ILogger<AutostartRegistration> log, string? configHome = null, Func<string?>? executable = null)
    : IStartupRegistration
{
    private string EntryPath => Path.Combine(
        configHome
            ?? (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")),
        "autostart", "copyloft.desktop");

    public void Apply(bool enabled)
    {
        try
        {
            var path = EntryPath;
            var legacy = Path.Combine(Path.GetDirectoryName(path)!, "clipshelf.desktop");   // the app's name up to 0.4.1
            if (File.Exists(legacy)) File.Delete(legacy);
            if (!enabled)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            var exe = (executable ?? (() => Environment.ProcessPath))();
            if (exe is null)
            {
                log.LogWarning("The executable path is unknown; the startup setting was not applied");
                return;
            }

            // A line break in the path would start a new key in the entry.
            if (exe.Any(char.IsControl))
            {
                log.LogWarning("The executable path has control characters; the startup setting was not applied");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"""
                [Desktop Entry]
                Type=Application
                Name=Copyloft
                Exec={Quote(exe)}
                Icon=copyloft
                Terminal=false
                X-GNOME-Autostart-enabled=true

                """);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Applying the startup setting failed");
        }
    }

    // Quoting rule first (backslash before " ` $ \), then the string rule doubles each backslash;
    // % is doubled so it is never read as a field code.
    internal static string Quote(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
            quoted.Append(c switch
            {
                '"' or '`' or '$' => @"\\" + c,
                '\\' => @"\\\\",
                '%' => "%%",
                _ => c.ToString(),
            });
        return quoted.Append('"').ToString();
    }
}
