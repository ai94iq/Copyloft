using Copyloft.Presentation.Hosting;

namespace Copyloft.Presentation.Tests;

public sealed class FileSettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("copyloft-settings").FullName;
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Save_round_trips()
    {
        var store = new FileSettingsStore(SettingsPath);

        store.Save(new AppSettings { MaxItems = 42 });

        Assert.Equal(42, store.Load().MaxItems);
        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    // The next save replaces the file, so what could not be read is kept aside first.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"Hotkey\": 5 }")]
    public void Corrupt_file_kept(string content)
    {
        File.WriteAllText(SettingsPath, content);

        var settings = new FileSettingsStore(SettingsPath).Load();

        Assert.Equal(new AppSettings().MaxItems, settings.MaxItems);
        Assert.Equal(content, File.ReadAllText(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Huge_file_ignored()
    {
        File.WriteAllText(SettingsPath, "{\"x\":\"" + new string('a', (int)FileSettingsStore.MaxBytes) + "\"}");

        Assert.Equal(new AppSettings().MaxItems, new FileSettingsStore(SettingsPath).Load().MaxItems);
    }
}
