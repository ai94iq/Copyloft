using Copyloft.Core.Services;

namespace Copyloft.Desktop.Platform;

// Turns clipboard changes into history entries and writes items back. Writes made by this app are
// counted so the resulting clipboard change does not get captured as a new item.
public sealed class ClipboardService : IClipboardMonitor, IDisposable
{
    private readonly IClipboardBackend _backend;
    private readonly ClipCaptureService _capture;
    private readonly IClipRepository _repository;
    private readonly SettingsService _settings;
    private readonly IForegroundApp _foregroundApp;
    private readonly ILogger<ClipboardService> _log;
    private int _selfWrites;

    public ClipboardService(
        IClipboardBackend backend,
        ClipCaptureService capture,
        IClipRepository repository,
        SettingsService settings,
        IForegroundApp foregroundApp,
        ILogger<ClipboardService> log)
    {
        _backend = backend;
        _capture = capture;
        _repository = repository;
        _settings = settings;
        _foregroundApp = foregroundApp;
        _log = log;
        _backend.Changed += OnClipboardChanged;
    }

    // The capture started by the latest change, for tests.
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public Task<bool> WriteText(string text) => WriteAsync(() => _backend.WriteTextAsync(text), "text");

    public Task<bool> WriteImageAsync(byte[] png) => WriteAsync(() => _backend.WriteImageAsync(png), "an image");

    public void Dispose()
    {
        _backend.Changed -= OnClipboardChanged;
        _backend.Dispose();
    }

    private async Task<bool> WriteAsync(Func<Task<bool>> write, string what)
    {
        _selfWrites++;
        if (await write()) return true;
        _selfWrites--;
        _log.LogWarning("Writing {What} to the clipboard failed", what);
        return false;
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        if (_selfWrites > 0)
        {
            _selfWrites--;
            return;
        }

        // The source app is read now, while the copy is fresh.
        Pending = CaptureAsync(_foregroundApp.Name());
    }

    private async Task CaptureAsync(string? appName)
    {
        try
        {
            // An image copy wins over any text formats the same copy may carry.
            if (await _backend.ReadImagePngAsync() is { } png)
            {
                await CaptureImageAsync(png, appName);
                return;
            }

            var text = await _backend.ReadTextAsync();
            if (text is null) return;

            await _capture.CaptureAsync(
                text,
                appName,
                DateTimeOffset.UtcNow,
                _settings.Current.MaxItems,
                CancellationToken.None);

            // Age-based cleanup rides along with captures so a long session stays trimmed.
            await PruneByAgeAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Capturing a clipboard change failed");
        }
    }

    private async Task CaptureImageAsync(byte[] png, string? appName)
    {
        // Imaging runs on a worker thread: it must never touch the UI thread.
        var thumbnail = await Task.Run(() => ImageClipCodec.Thumbnail(png)) ?? png;
        await _capture.CaptureImageAsync(
            png,
            thumbnail,
            appName,
            DateTimeOffset.UtcNow,
            _settings.Current.MaxItems,
            CancellationToken.None);
        await PruneByAgeAsync();
    }

    private async Task PruneByAgeAsync()
    {
        var days = _settings.Current.RetentionDays;
        if (days > 0)
            await _repository.PruneOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-days), CancellationToken.None);
    }
}
