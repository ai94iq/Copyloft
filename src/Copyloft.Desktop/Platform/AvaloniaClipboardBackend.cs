using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Copyloft.Desktop.Platform;

// Reading and writing through Avalonia's clipboard, which handles selection ownership and large
// transfers. The OS layer only reports changes through NotifyChanged: XFixes on X11, a
// pasteboard poll on macOS. Images travel as PNG under the platform's own name for it.
public sealed class AvaloniaClipboardBackend(Func<IClipboard?> clipboard, string pngFormat) : IClipboardBackend
{
    // image/png on X11, public.png on macOS.
    private readonly DataFormat<byte[]> _png = DataFormat.CreateBytesPlatformFormat(pngFormat);

    public event EventHandler? Changed;

    // On the UI thread.
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // Markers password managers put next to a secret: the nspasteboard.org types on macOS, and
    // KDE's hint (KeePassXC, KWallet) on X11.
    private static readonly HashSet<string> ExcludedFormats =
    [
        "org.nspasteboard.ConcealedType",
        "org.nspasteboard.TransientType",
        "x-kde-passwordManagerHint",
    ];

    internal static bool IsExcluded(IEnumerable<string> formatNames) => formatNames.Any(ExcludedFormats.Contains);

    public async Task<bool> IsExcludedAsync() =>
        clipboard() is { } board && IsExcluded((await board.GetDataFormatsAsync()).Select(format => format.Identifier));

    public async Task<string?> ReadTextAsync() =>
        clipboard() is { } board ? await board.TryGetTextAsync() : null;

    // Larger than any real screenshot: such an image is skipped rather than stored. Avalonia has
    // already transferred it by then; the bound keeps it out of the history and the decoder.
    internal const int MaxPngBytes = 128 * 1024 * 1024;

    public async Task<byte[]?> ReadImagePngAsync() =>
        clipboard() is { } board && await board.TryGetValueAsync(_png) is { Length: <= MaxPngBytes } png ? png : null;

    public async Task<bool> WriteTextAsync(string text)
    {
        if (clipboard() is not { } board) return false;
        await board.SetTextAsync(text);
        return true;
    }

    public async Task<bool> WriteImageAsync(byte[] png)
    {
        if (clipboard() is not { } board) return false;
        await board.SetValueAsync(_png, png);
        return true;
    }

    public void Dispose() { }
}
