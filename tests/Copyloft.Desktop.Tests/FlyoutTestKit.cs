using Avalonia;
using Copyloft.Core.Abstractions;
using Copyloft.Core.Models;
using Copyloft.Desktop.Platform;
using Copyloft.Desktop.Shell;
using Copyloft.Presentation.Features.ClipboardPanel;
using Copyloft.Presentation.Platform;
using Copyloft.Presentation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Copyloft.Desktop.Tests;

// A real ClipboardPanelViewModel over a faked store and clipboard. The store returns whatever
// the test added, the way the real one would, and reports StoredTotal as its count.
internal class ViewModelKit
{
    private readonly List<ClipListItem> _stored = [];

    public IClipboardWriter Clipboard { get; } = Substitute.For<IClipboardWriter>();
    public IClipRepository Repository { get; } = Substitute.For<IClipRepository>();
    public ICategoryRepository Categories { get; } = Substitute.For<ICategoryRepository>();
    public ICategoryLockService Locks { get; } = Substitute.For<ICategoryLockService>();
    public ILockPasswordService Passwords { get; } = Substitute.For<ILockPasswordService>();
    public List<Category> StoredCategories { get; } = [];
    public ClipboardPanelViewModel ViewModel { get; }
    public int? StoredTotal { get; set; }

    public ViewModelKit()
    {
        Repository.GetRecentAsync(default, default, default)
            .ReturnsForAnyArgs(_ => Task.FromResult<IReadOnlyList<ClipListItem>>(
                _stored.OrderByDescending(clip => clip.IsPinned).ToList()));
        Repository.CountAsync(default, default, default, default).ReturnsForAnyArgs(_ => StoredTotal ?? _stored.Count);
        Categories.GetCategoriesAsync(default).ReturnsForAnyArgs(_ => Task.FromResult<IReadOnlyList<Category>>(StoredCategories.ToList()));
        Locks.UnlockedCategoryIds.Returns([]);
        Clipboard.WriteText(default!).ReturnsForAnyArgs(true);
        Clipboard.WriteImageAsync(default!).ReturnsForAnyArgs(true);
        ViewModel = new ClipboardPanelViewModel(
            Repository, Categories, Locks, Passwords, Clipboard, Substitute.For<IDateFormatter>(),
            NullLogger<ClipboardPanelViewModel>.Instance);
    }

    // Stored only; a refresh shows it.
    public void StoreClip(string text, bool pinned = false) =>
        _stored.Add(new ClipListItem(_stored.Count + 1, text, "Notepad", pinned, DateTimeOffset.UtcNow,
            new PageCursor(DateTimeOffset.UtcNow.ToString("O"), _stored.Count + 1)));

    // Stored and shown at once.
    public void AddClip(string text)
    {
        StoreClip(text);
        ViewModel.Items.Add(new ClipItemViewModel(_stored[^1], Substitute.For<IDateFormatter>(), ViewModel));
    }
}

// A flyout over a ViewModelKit with a faked platform and paste-back.
internal sealed class FlyoutTestKit : ViewModelKit
{
    public FakeFlyoutPlatform Platform { get; } = new();
    public IPasteBack PasteBack { get; } = Substitute.For<IPasteBack>();
    public ManualTime Time { get; } = new();
    public FlyoutWindow Window { get; }

    public FlyoutTestKit(bool canPaste = true)
    {
        PasteBack.CanPaste.Returns(canPaste);
        Window = new FlyoutWindow(ViewModel, Platform, PasteBack, Time);
    }
}

internal sealed class FakeFlyoutPlatform : IFlyoutPlatform
{
    public IntPtr Foreground { get; set; } = new(42);
    public PixelPoint Pointer { get; set; } = new(100, 100);
    public IntPtr? RestoredTo { get; private set; }

    public IntPtr ForegroundWindow() => Foreground;

    public PixelPoint Cursor() => Pointer;

    public void ForceForeground(IntPtr window) { }

    public void RestoreForeground(IntPtr own, IntPtr previous) => RestoredTo = previous;
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
