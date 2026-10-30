using Copyloft.Desktop.Platform.Windows;

namespace Copyloft.Desktop.Tests;

// The Run key as a dictionary; the real registry is never written.
public sealed class StartupRegistrationTests
{
    private static Dictionary<string, string> Apply(bool enabled, params (string Name, string Value)[] existing)
    {
        var values = existing.ToDictionary(v => v.Name, v => v.Value);
        if (OperatingSystem.IsWindows())
            StartupRegistration.Apply(enabled, @"C:\Program Files\Copyloft\Copyloft.exe",
                (name, value) => values[name] = value, name => values.Remove(name));
        return values;
    }

    [Fact]
    public void Enabling_runs_this_executable_and_removes_the_ClipShelf_value()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows");

        var values = Apply(true, ("ClipShelf", @"""C:\Program Files\ClipShelf\ClipShelf.exe"""), ("Other", "x"));

        Assert.Equal(@"""C:\Program Files\Copyloft\Copyloft.exe""", values["Copyloft"]);
        Assert.False(values.ContainsKey("ClipShelf"));
        Assert.Equal("x", values["Other"]);
    }

    [Fact]
    public void Disabling_removes_both_values()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows");

        var values = Apply(false, ("ClipShelf", "old"), ("Copyloft", "new"));

        Assert.Empty(values);
    }

    [Fact]
    public void No_path_keeps_value()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows");
        var values = new Dictionary<string, string> { ["Copyloft"] = @"""C:\old\Copyloft.exe""" };

        if (OperatingSystem.IsWindows())
            StartupRegistration.Apply(true, null, (name, value) => values[name] = value, name => values.Remove(name));

        Assert.Equal(@"""C:\old\Copyloft.exe""", values["Copyloft"]);
    }
}
