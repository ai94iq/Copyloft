using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Copyloft.Desktop.Controls;

namespace Copyloft.Desktop.Features;

// The History page of the main window: the full list, grouped into Pinned and Recent.
public sealed partial class HistoryPage : UserControl
{
    // Within about one row of the end, the next page loads.
    private const double LoadMoreThreshold = 48;

    private long? _lastFilterValue;

    public HistoryPage(ClipboardPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        ClipList.ItemsSource = new GroupedRows(viewModel.Groups);

        // Group headers are labels, not rows.
        ClipList.ContainerPrepared += (_, e) =>
        {
            var isHeader = e.Container.DataContext is ClipGroup;
            e.Container.IsEnabled = !isHeader;
            e.Container.Focusable = !isHeader;
        };
        ClipList.AddHandler(PointerReleasedEvent, OnListClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        ClipList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrolled);
        CategoryFilter.SelectionChanged += (_, _) => UiTask.Run(OnCategoryFilterChangedAsync, "Changing the category filter");
        // Picking the locked category that is already selected changes nothing; ask again.
        CategoryFilter.DropDownClosed += (_, _) => UiTask.Run(UnlockSelectedFilterAsync, "Unlocking the category filter");
        viewModel.CategoryMenuRequested += item => _ = CategoryMenu.ShowAsync(CategoryMenu.Anchor(ClipList, item), item, viewModel);
    }

    public ClipboardPanelViewModel ViewModel { get; }

    public Task LoadAsync() => Task.WhenAll(ViewModel.RefreshAsync(), ViewModel.RefreshCategoryFilterAsync());

    // Keys are taken at the window, so Esc works even when nothing on the page has focus yet.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnKeyDown);
        base.OnDetachedFromVisualTree(e);
    }

    public static bool IsNearEnd(double offset, double viewport, double extent) =>
        offset + viewport >= extent - LoadMoreThreshold;

    // Enter copies the keyboard row; Esc leaves select mode or cancels the confirm, never more.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (InlineDialog.IsOpenIn(this)) return;     // Enter and Esc belong to the dialog

        if (e.Key == Key.Enter && ClipList.SelectedItem is ClipItemViewModel item && ClipList.IsKeyboardFocusWithin)
        {
            ViewModel.Activate(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = ViewModel.DismissTransient();
        }
    }

    // Click-to-copy: the accent tint belongs to the keyboard row and select mode, so a click
    // does not leave the row selected. Clicks on the row buttons stay with the buttons.
    private void OnListClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source) return;
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not ClipItemViewModel item) return;

        ViewModel.Activate(item);
        if (!ViewModel.IsSelecting) ClipList.SelectedItem = null;
    }

    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroller) return;
        if (!IsNearEnd(scroller.Offset.Y, scroller.Viewport.Height, scroller.Extent.Height)) return;
        if (ViewModel is IIncrementalSource { HasMore: true } source && source.LoadMoreCommand.CanExecute(null))
            source.LoadMoreCommand.Execute(null);
    }

    // Picking a locked category asks for its password instead of showing an empty list.
    private async Task OnCategoryFilterChangedAsync()
    {
        var selected = ViewModel.SelectedCategoryFilter;
        if (selected is null || selected.Value == _lastFilterValue) return;

        if (!ViewModel.IsCategoryFilterLocked)
        {
            _lastFilterValue = selected.Value;
            return;
        }

        if (await UnlockSelectedFilterAsync()) return;

        ViewModel.SelectedCategoryFilter =
            ViewModel.CategoryFilterOptions.FirstOrDefault(option => option.Value == _lastFilterValue)
            ?? ViewModel.CategoryFilterOptions[0];
    }

    private async Task<bool> UnlockSelectedFilterAsync()
    {
        if (ViewModel.SelectedCategoryFilter?.Value is not long id || !ViewModel.IsCategoryFilterLocked) return false;
        if (InlineDialog.IsOpenIn(this)) return false;
        if (!await PasswordPrompt.VerifyAsync(this, ViewModel.VerifyLockPassword, () => ViewModel.LockPasswordRetryAfter)) return false;

        _lastFilterValue = id;
        await ViewModel.UnlockCategoryAsync(id);
        return true;
    }
}
