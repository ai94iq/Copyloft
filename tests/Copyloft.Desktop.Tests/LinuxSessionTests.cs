using Copyloft.Desktop.Platform;
using Copyloft.Desktop.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;

namespace Copyloft.Desktop.Tests;

public sealed class LinuxSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("copyloft-autostart").FullName;
    private string EntryPath => Path.Combine(_dir, "autostart", "copyloft.desktop");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AutostartRegistration Autostart(string exe) =>
        new(NullLogger<AutostartRegistration>.Instance, _dir, () => exe);

    [Fact]
    public void Enabling_writes_an_autostart_entry_that_runs_this_executable()
    {
        Autostart("/opt/copy loft/Copyloft").Apply(true);

        var lines = File.ReadAllLines(EntryPath);
        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains("Exec=\"/opt/copy loft/Copyloft\"", lines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Applying_removes_the_ClipShelf_entry(bool enabled)
    {
        var legacy = Path.Combine(_dir, "autostart", "clipshelf.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "[Desktop Entry]");

        Autostart("/usr/bin/copyloft").Apply(enabled);

        Assert.False(File.Exists(legacy));
        Assert.Equal(enabled, File.Exists(EntryPath));
    }

    // Desktop Entry spec: inside quotes ", `, $ and \ are backslash-escaped, and the string rule
    // doubles every backslash again; % is doubled for the field-code rule.
    [Fact]
    public void Exec_escapes_what_the_desktop_entry_spec_reserves()
    {
        Assert.Equal("\"/a\\\\\"b\\\\`c\\\\$d\\\\\\\\e%%f\"", AutostartRegistration.Quote("/a\"b`c$d\\e%f"));
    }

    // A line break in the path would start a new key in the entry.
    [Fact]
    public void Newline_path_not_written()
    {
        Autostart("/opt/x\nExec=evil").Apply(true);

        Assert.False(File.Exists(EntryPath));
    }

    [Fact]
    public void Disabling_removes_the_entry_and_tolerates_a_missing_one()
    {
        var autostart = Autostart("/usr/bin/copyloft");
        autostart.Apply(true);

        autostart.Apply(false);
        autostart.Apply(false);

        Assert.False(File.Exists(EntryPath));
    }

    [Fact]
    public void Termination_raises_ending_before_the_process_exits()
    {
        var order = new List<string>();
        var session = new PosixSessionEnd(code => order.Add($"exit {code}"));
        session.Ending += (_, _) => order.Add("ending");

        session.OnTerminate();

        Assert.Equal(["ending", "exit 0"], order);
    }
}
