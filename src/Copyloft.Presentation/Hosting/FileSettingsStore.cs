using System.Text.Json;

namespace Copyloft.Presentation.Hosting;

// settings.json under %LOCALAPPDATA%, written atomically. Another path reads a different install's
// settings without touching AppPaths (Program reads ClipShelf's while ClipShelf runs).
public sealed class FileSettingsStore(string? path = null) : ISettingsStore
{
    // Real settings are a few kilobytes; anything far larger is not a settings file.
    internal const long MaxBytes = 1024 * 1024;

    private string SettingsPath => path ?? AppPaths.Settings;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // A file that cannot be read as settings loads as defaults, and a copy is kept as
    // settings.json.corrupt first, since the next save replaces it.
    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();

        try
        {
            if (new FileInfo(SettingsPath).Length > MaxBytes) throw new InvalidDataException("settings.json is too large");
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException
                                       or FormatException or InvalidDataException)
        {
            KeepCorruptCopy();
            return new();
        }
    }

    // A unique temporary name created fresh, so nothing planted at a fixed name is followed.
    public void Save(AppSettings settings)
    {
        var temp = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, settings, Options);
        File.Move(temp, SettingsPath, overwrite: true);     // atomic replace
    }

    private void KeepCorruptCopy()
    {
        try
        {
            File.Copy(SettingsPath, SettingsPath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Defaults still load; only the copy is lost.
        }
    }
}
