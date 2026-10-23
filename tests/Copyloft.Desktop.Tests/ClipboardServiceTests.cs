using Copyloft.Core.Abstractions;
using Copyloft.Core.Services;
using Copyloft.Desktop.Platform;
using Copyloft.Presentation.Hosting;
using Copyloft.Presentation.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Copyloft.Desktop.Tests;

// The capture rules every OS shares; the backend (Win32, X11) is faked.
public sealed class ClipboardServiceTests
{
    private readonly FakeBackend _backend = new();
    private readonly IClipRepository _repository = Substitute.For<IClipRepository>();
    private readonly ClipboardService _service;

    public ClipboardServiceTests()
    {
        var app = Substitute.For<IForegroundApp>();
        app.Name().Returns("editor");
        _service = new ClipboardService(
            _backend,
            new ClipCaptureService(_repository),
            _repository,
            new SettingsService(Substitute.For<ISettingsStore>(), new AppSettings()),
            app,
            NullLogger<ClipboardService>.Instance);
    }

    [Fact]
    public async Task A_text_copy_is_captured_with_the_app_it_came_from()
    {
        _backend.Text = "hello";

        await _backend.ChangeAsync(_service);

        await _repository.Received(1).AddOrBumpAsync("hello", "editor", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_image_copy_wins_over_its_text()
    {
        using var bitmap = new SKBitmap(2, 2);
        _backend.Png = bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        _backend.Text = "file name";

        await _backend.ChangeAsync(_service);

        await _repository.Received(1).AddImageAsync(_backend.Png, Arg.Any<byte[]>(), "editor", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceiveWithAnyArgs().AddOrBumpAsync(default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_change_our_own_write_causes_is_not_captured_but_the_next_one_is()
    {
        await _service.WriteText("from history");
        _backend.Text = "from history";
        await _backend.ChangeAsync(_service);

        _backend.Text = "typed later";
        await _backend.ChangeAsync(_service);

        await _repository.DidNotReceive().AddOrBumpAsync("from history", Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).AddOrBumpAsync("typed later", "editor", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_write_does_not_swallow_the_next_change()
    {
        _backend.WritesSucceed = false;
        await _service.WriteText("lost");
        await _service.WriteImageAsync([1, 2, 3]);

        _backend.Text = "real copy";
        await _backend.ChangeAsync(_service);

        await _repository.Received(1).AddOrBumpAsync("real copy", "editor", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    private sealed class FakeBackend : IClipboardBackend
    {
        public string? Text { get; set; }
        public byte[]? Png { get; set; }
        public bool WritesSucceed { get; set; } = true;

        public event EventHandler? Changed;

        public async Task ChangeAsync(ClipboardService service)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            await service.Pending;
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(Text);
        public Task<byte[]?> ReadImagePngAsync() => Task.FromResult(Png);
        public Task<bool> WriteTextAsync(string text) => Task.FromResult(WritesSucceed);
        public Task<bool> WriteImageAsync(byte[] png) => Task.FromResult(WritesSucceed);
        public void Dispose() { }
    }
}
