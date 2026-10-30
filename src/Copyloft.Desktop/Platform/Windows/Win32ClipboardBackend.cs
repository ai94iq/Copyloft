using System.Runtime.Versioning;

namespace Copyloft.Desktop.Platform.Windows;

// The Win32 clipboard: the format listener for changes, DIB blocks converted to and from PNG.
[SupportedOSPlatform("windows")]
public sealed class Win32ClipboardBackend(ILogger<Win32ClipboardBackend> log) : IClipboardBackend
{
    private readonly ClipboardWatcher _watcher = new();

    public event EventHandler? Changed
    {
        add => _watcher.Changed += value;
        remove => _watcher.Changed -= value;
    }

    public Task<bool> IsExcludedAsync() => Task.Run(Win32Clipboard.IsExcluded);

    // The text read can sleep on retries, so it runs off the UI thread.
    public Task<string?> ReadTextAsync() => Task.Run(Win32Clipboard.TryReadText);

    public async Task<byte[]?> ReadImagePngAsync()
    {
        var dib = await Task.Run(() => Win32Clipboard.HasImage() ? Win32Clipboard.TryReadImage() : null);
        if (dib is null) return null;

        var png = await Task.Run(() => ImageClipCodec.ToPng(dib));
        if (png is null)
            log.LogWarning("An image copy could not be decoded ({Bytes} bytes); it was not saved", dib.Length);
        return png;
    }

    public Task<bool> WriteTextAsync(string text) => Task.FromResult(Win32Clipboard.TryWriteText(text));

    public Task<bool> WriteImageAsync(byte[] png) =>
        Task.Run(() => ImageClipCodec.ToDib(png) is { } dib && Win32Clipboard.TryWriteImage(dib));

    public void Dispose() => _watcher.Dispose();
}
