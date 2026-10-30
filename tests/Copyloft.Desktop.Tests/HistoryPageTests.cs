using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Copyloft.Core.Models;
using Copyloft.Desktop.Features;
using Copyloft.Presentation.Features.ClipboardPanel;
using NSubstitute;
using Copyloft.Presentation.Localization;
using Avalonia;

namespace Copyloft.Desktop.Tests;

public sealed class HistoryPageTests
{
    private sealed record Hosted(ViewModelKit Kit, HistoryPage Page, Window Window)
    {
        public ListBox List => Page.FindControl<ListBox>("ClipList")!;

        public void Press(Key key)
        {
            Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<Hosted> Open(ViewModelKit kit)
    {
        var page = new HistoryPage(kit.ViewModel);
        var window = new Window { Content = page, Width = 800, Height = 600 };
        window.Show();
        await page.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        return new Hosted(kit, page, window);
    }

    private static ViewModelKit Clips(params string[] texts)
    {
        var kit = new ViewModelKit();
        foreach (var text in texts) kit.StoreClip(text);
        return kit;
    }

    [Fact]
    public Task Group_headers_cannot_be_selected_or_focused() => Ui.Run(async () =>
    {
        var hosted = await Open(Clips("one", "two"));

        var header = hosted.List.ContainerFromIndex(0)!;

        Assert.IsType<ClipGroup>(header.DataContext);
        Assert.False(header.IsEnabled);
        Assert.False(header.Focusable);
    });

    [Fact]
    public Task Enter_on_a_row_copies_it_and_says_so() => Ui.Run(async () =>
    {
        var hosted = await Open(Clips("one", "two"));
        hosted.List.SelectedIndex = 2;                       // [Recent], one, two
        hosted.List.ContainerFromIndex(2)!.Focus();

        hosted.Press(Key.Enter);

        _ = hosted.Kit.Clipboard.Received().WriteText("two");
        var notice = hosted.Page.FindControl<TextBlock>("CopyNotice")!;
        Assert.True(notice.IsVisible);
        Assert.Equal("Copied", notice.Text);
    });

    // The owner's call: History's actions are icons with tooltips beside the search, as in the
    // flyout, instead of a full Select button next to a bare trash icon in the footer.
    private static Button Action(Hosted hosted, string name) => hosted.Page.FindControl<Button>(name)!;

    [Theory]
    [InlineData("SelectButton", "Checkmark", "Selection_Select")]
    [InlineData("ClearAllButton", "Delete", "Panel_ClearAll")]
    public Task History_actions_are_icons_beside_the_search(string name, string icon, string textKey) => Ui.Run(async () =>
    {
        var hosted = await Open(Clips("one"));
        hosted.Window.UpdateLayout();
        var button = Action(hosted, name);
        var search = hosted.Page.FindControl<TextBox>("SearchBox")!;

        Assert.Contains("glyph", button.Classes);
        Assert.Equal(icon, button.GetVisualDescendants().OfType<Copyloft.Desktop.Controls.AppIcon>().Single().Kind.ToString());
        Assert.Equal(Tr.Get(textKey), ToolTip.GetTip(button));
        Assert.Equal(Tr.Get(textKey), Avalonia.Automation.AutomationProperties.GetName(button));
        var top = (Func<Visual, double>)(v => v.TranslatePoint(new Point(0, 0), hosted.Page)!.Value.Y);
        Assert.InRange(top(button) - top(search), -2, 2);
        Assert.Empty(hosted.Page.FindControl<Border>("Footer")!.GetVisualDescendants().OfType<Button>());
    });

    [Theory]
    [InlineData("CopySelectedButton", "Copy", "Selection_Copy")]
    [InlineData("CancelSelectButton", "Dismiss", "Common_Cancel")]
    public Task Select_mode_actions_are_icons_too(string name, string icon, string textKey) => Ui.Run(async () =>
    {
        var hosted = await Open(Clips("one"));
        hosted.Kit.ViewModel.ToggleSelectionCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var button = Action(hosted, name);

        Assert.True(button.IsVisible);
        Assert.Contains("glyph", button.Classes);
        Assert.Equal(icon, button.GetVisualDescendants().OfType<Copyloft.Desktop.Controls.AppIcon>().Single().Kind.ToString());
        Assert.Equal(Tr.Get(textKey), ToolTip.GetTip(button));
    });

    // Seen on macOS: a disabled action drew Fluent's gray block.
    [Fact]
    public Task An_empty_history_fades_its_actions_without_a_gray_block() => Ui.Run(async () =>
    {
        var hosted = await Open(new ViewModelKit());
        hosted.Window.UpdateLayout();

        foreach (var button in new[] { Action(hosted, "SelectButton"), Action(hosted, "ClearAllButton") })
        {
            Assert.False(button.IsEffectivelyEnabled);
            var fill = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Background;
            Assert.True(fill is null || fill is Avalonia.Media.ISolidColorBrush { Color.A: 0 }, $"{button.Name} has no fill");
        }
    });

    [Fact]
    public Task The_footer_counts_every_stored_clip_not_just_the_loaded_ones() => Ui.Run(async () =>
    {
        var kit = Clips("one", "two");
        kit.StoredTotal = 532;

        var hosted = await Open(kit);

        Assert.Equal("532 clips", hosted.Page.FindControl<TextBlock>("FooterText")!.Text);
    });

    [Fact]
    public Task Escape_leaves_select_mode_then_cancels_the_confirm_and_never_more() => Ui.Run(async () =>
    {
        var hosted = await Open(Clips("one"));
        var vm = hosted.Kit.ViewModel;

        vm.ToggleSelectionCommand.Execute(null);
        hosted.Press(Key.Escape);
        Assert.False(vm.IsSelecting);

        vm.RequestClearCommand.Execute(null);
        hosted.Press(Key.Escape);
        Assert.False(vm.IsConfirmingClear);
    });

    [Theory]
    [InlineData(0, 500, 2000, false)]
    [InlineData(1460, 500, 2000, true)]          // within a row of the end
    [InlineData(1500, 500, 2000, true)]
    public void More_rows_load_near_the_end_of_the_scroll(double offset, double viewport, double extent, bool expected) =>
        Assert.Equal(expected, HistoryPage.IsNearEnd(offset, viewport, extent));

    [Fact]
    public Task A_locked_category_in_the_filter_asks_for_the_password() => Ui.Run(async () =>
    {
        var kit = Clips("one");
        kit.StoredCategories.Add(new Category(5, "Work", IsLocked: true));
        kit.Passwords.Verify("1234").Returns(true);
        var hosted = await Open(kit);

        hosted.Page.FindControl<ComboBox>("CategoryFilter")!.SelectedIndex = 1;   // "Work (locked)"
        var dialog = DialogDriver.Open(hosted.Window);
        DialogDriver.Boxes(dialog)[0].Text = "1234";
        DialogDriver.Click(dialog.FindControl<Button>("PrimaryButton")!);

        kit.Locks.Received().Unlock(5);
        Assert.Equal(5, kit.ViewModel.SelectedCategoryFilter!.Value);
    });

    [Fact]
    public Task Cancelling_the_password_falls_back_to_the_previous_filter() => Ui.Run(async () =>
    {
        var kit = Clips("one");
        kit.StoredCategories.Add(new Category(5, "Work", IsLocked: true));
        var hosted = await Open(kit);

        hosted.Page.FindControl<ComboBox>("CategoryFilter")!.SelectedIndex = 1;
        DialogDriver.Click(DialogDriver.Open(hosted.Window).FindControl<Button>("CancelButton")!);

        Assert.Null(kit.ViewModel.SelectedCategoryFilter!.Value);
        kit.Locks.DidNotReceive().Unlock(Arg.Any<long>());
    });

    [Fact]
    public Task Keys_belong_to_an_open_dialog() => Ui.Run(async () =>
    {
        var kit = Clips("one");
        var hosted = await Open(kit);
        kit.ViewModel.ToggleSelectionCommand.Execute(null);
        _ = new Copyloft.Desktop.Controls.InlineDialog("t", "ok").ShowAsync(hosted.Window);
        Dispatcher.UIThread.RunJobs();

        hosted.Press(Key.Escape);

        Assert.True(kit.ViewModel.IsSelecting);    // Esc closed the dialog, not select mode
        Assert.Null(Copyloft.Desktop.Controls.InlineDialog.OpenIn(hosted.Window));
    });
}
