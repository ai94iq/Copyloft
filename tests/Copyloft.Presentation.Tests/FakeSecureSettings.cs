using Copyloft.Core.Abstractions;

namespace Copyloft.Presentation.Tests;

// In-memory stand-in for the Setting table.
public sealed class FakeSecureSettings : ISecureSettings
{
    public Dictionary<string, string> Values { get; } = [];

    public string? Get(string key) => Values.GetValueOrDefault(key);

    public void Set(string key, string? value)
    {
        if (value is null) Values.Remove(key);
        else Values[key] = value;
    }
}

// A clock the test moves by hand.
public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
