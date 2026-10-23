using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Copyloft.Desktop.Controls;
using NSubstitute;
using SkiaSharp;

namespace Copyloft.Desktop.Tests;

public sealed class FlyoutListTests
{
    private static void Press(FlyoutTestKit kit, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        kit.Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        kit.Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static FlyoutTestKit OpenWith(params string[] clips) => OpenWith(canPaste: true, clips);

    private static FlyoutTestKit OpenWith(bool canPaste, params string[] clips)
    {
        var kit = new FlyoutTestKit(canPaste);
        foreach (var clip in clips) kit.AddClip(clip);
        kit.Window.ShowFlyout();
        Dispatcher.UIThread.RunJobs();
        return kit;
    }

    [Fact]
    public Task Typing_goes_to_the_search_box() => Ui.Run(() =>
    {
        var kit = OpenWith("alpha");

        kit.Window.KeyTextInput("al");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("al", kit.ViewModel.SearchText);
    });

    // Keys arrive while the search box has focus.
    [Fact]
    public Task Enter_in_an_open_dialog_does_not_paste() => Ui.Run(async () =>
    {
        var kit = OpenWith("first");
        var dialog = new InlineDialog("New category", "Create", new TextBox()).ShowAsync(kit.Window);
        Dispatcher.UIThread.RunJobs();

        Press(kit, Key.Enter);

        Assert.True(await dialog.WaitAsync(TimeSpan.FromSeconds(5)));
        _ = kit.PasteBack.DidNotReceiveWithAnyArgs().IntoAsync(default);
        Assert.True(kit.Window.IsOpen);
    });

    // Wayland: no keystroke can reach other apps, so the pick is copied and a hint says how to paste.
    [Fact]
    public Task Where_paste_back_is_impossible_the_pick_is_copied_with_a_hint() => Ui.Run(() =>
    {
        var kit = OpenWith(canPaste: false, "first");

        Press(kit, Key.Enter);

        _ = kit.Clipboard.Received().WriteText("first");
        _ = kit.PasteBack.DidNotReceiveWithAnyArgs().IntoAsync(default);
        Assert.False(kit.Window.IsOpen);
        Assert.True(kit.Window.Hint?.IsVisible);
        Assert.Contains(OperatingSystem.IsMacOS() ? "Cmd+V" : "Ctrl+V", kit.Window.Hint!.Text);
        var frame = Assert.IsType<Border>(kit.Window.Hint.Content);
        Assert.True(frame.BorderThickness.Left > 0 && frame.BorderBrush is not null, "the hint needs a visible edge");
    });

    [Fact]
    public Task Down_then_enter_pastes_that_clip_into_the_previous_window() => Ui.Run(() =>
    {
        var kit = OpenWith("first", "second");

        Press(kit, Key.Down);
        Press(kit, Key.Down);
        Press(kit, Key.Enter);

        _ = kit.Clipboard.Received().WriteText("second");
        kit.PasteBack.Received().IntoAsync(new IntPtr(42));
        Assert.False(kit.Window.IsOpen);
    });

    // A slow write (an image load) that ends after the user closed the flyout sends no keystroke.
    [Fact]
    public Task A_pick_that_finishes_after_the_flyout_closed_does_not_paste() => Ui.Run(() =>
    {
        var kit = OpenWith("first");
        var write = new TaskCompletionSource<bool>();
        kit.Clipboard.WriteText("first").Returns(write.Task);

        Press(kit, Key.Enter);
        kit.Window.HideFlyout();
        write.SetResult(true);
        Dispatcher.UIThread.RunJobs();

        _ = kit.PasteBack.DidNotReceiveWithAnyArgs().IntoAsync(default);
    });

    [Fact]
    public Task Ctrl_and_a_digit_picks_that_row() => Ui.Run(() =>
    {
        var kit = OpenWith("first", "second", "third");

        Press(kit, Key.D2, RawInputModifiers.Control);

        _ = kit.Clipboard.Received().WriteText("second");
    });

    [Fact]
    public Task Escape_leaves_select_mode_before_it_closes() => Ui.Run(() =>
    {
        var kit = OpenWith("first");
        kit.ViewModel.ToggleSelectionCommand.Execute(null);

        Press(kit, Key.Escape);
        Assert.False(kit.ViewModel.IsSelecting);
        Assert.True(kit.Window.IsOpen);

        Press(kit, Key.Escape);
        Assert.False(kit.Window.IsOpen);
    });

    [Fact]
    public Task Every_row_shows_its_preview() => Ui.Run(() =>
    {
        var kit = OpenWith("alpha", "beta");

        var list = kit.Window.FindControl<ListBox>("ClipList")!;

        Assert.Equal(2, list.ItemCount);
    });

    [Fact]
    public Task A_png_becomes_a_bitmap_and_garbage_becomes_nothing() => Ui.Run(() =>
    {
        using var source = new SKBitmap(4, 4);
        var png = source.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var converter = BytesToBitmapConverter.Instance;

        Assert.IsType<Bitmap>(converter.Convert(png, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(converter.Convert(null, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(converter.Convert(new byte[] { 1, 2, 3 }, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture));
    });
}
