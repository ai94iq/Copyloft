using Copyloft.Presentation.Features.ClipboardPanel;
using Copyloft.Presentation.Localization;
using Copyloft.Presentation.Services;
using Copyloft.Core.Abstractions;
using Copyloft.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using NSubstitute;

namespace Copyloft.Presentation.Tests;

public sealed class ClipboardPanelViewModelTests
{
    private readonly IClipRepository _repository = Substitute.For<IClipRepository>();
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly ICategoryLockService _locks = new FakeCategoryLocks();
    private readonly ILockPasswordService _passwords = Substitute.For<ILockPasswordService>();
    private readonly IClipboardWriter _clipboard = Substitute.For<IClipboardWriter>();
    private readonly IDateFormatter _dates = Substitute.For<IDateFormatter>();

    public ClipboardPanelViewModelTests()
    {
        _dates.FormatSince(Arg.Any<DateTimeOffset>()).Returns("now");
        _clipboard.WriteText(default!).ReturnsForAnyArgs(true);
        _clipboard.WriteImageAsync(default!).ReturnsForAnyArgs(true);
    }

    [Fact]
    public async Task Loads_recent_clips()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two") });
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(LoadState.Loaded, vm.State);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("one", vm.Items[0].Text);
    }

    [Fact]
    public async Task No_clips_gives_empty_state()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>());
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(LoadState.Empty, vm.State);
    }

    [Fact]
    public async Task Search_uses_the_search_query()
    {
        _repository.SearchAsync("mail", null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "email") });
        var vm = Create();
        vm.SearchText = "mail";

        await vm.RefreshAsync();

        await _repository.Received(1).SearchAsync("mail", null, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Quiet_refresh_keeps_existing_rows_when_the_result_is_unchanged()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two") });
        var vm = Create();
        await vm.RefreshAsync();
        var first = vm.Items[0];
        var collectionChanges = 0;
        vm.Items.CollectionChanged += (_, _) => collectionChanges++;

        await vm.RefreshQuietAsync();

        Assert.Equal(0, collectionChanges);
        Assert.Same(first, vm.Items[0]);
    }

    [Fact]
    public async Task Quiet_refresh_sets_the_result_state_without_showing_loading()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one") });
        var vm = Create();
        var loadingObserved = false;
        vm.PropertyChanged += (_, args) =>
            loadingObserved |= args.PropertyName == nameof(vm.State) && vm.State == LoadState.Loading;

        await vm.RefreshQuietAsync();

        Assert.False(loadingObserved);
        Assert.Equal(LoadState.Loaded, vm.State);
    }

    [Fact]
    public async Task Quiet_refresh_inserts_new_rows_without_resetting_the_collection()
    {
        IReadOnlyList<ClipListItem> results = [Item(1, "one"), Item(2, "two")];
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(results));
        var vm = Create();
        await vm.RefreshAsync();
        var first = vm.Items[0];
        var second = vm.Items[1];
        var resetCollection = false;
        vm.Items.CollectionChanged += (_, args) =>
            resetCollection |= args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;
        results = [Item(3, "new"), Item(1, "one"), Item(2, "two")];

        await vm.RefreshQuietAsync();

        Assert.False(resetCollection);
        Assert.Equal(new long[] { 3, 1, 2 }, vm.Items.Select(item => item.Id));
        Assert.Same(first, vm.Items[1]);
        Assert.Same(second, vm.Items[2]);
    }

    [Fact]
    public async Task Activating_an_item_copies_it_and_signals_the_window()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();
        var signalled = false;
        vm.ItemActivated += (_, _) => signalled = true;

        vm.Activate(vm.Items[0]);

        _ = _clipboard.Received(1).WriteText("hello");
        Assert.True(signalled);
    }

    [Fact]
    public async Task Toggling_pin_flips_the_stored_flag()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();

        await vm.TogglePinAsync(vm.Items[0]);

        await _repository.Received(1).SetPinnedAsync(1, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_row_hides_it_at_once_and_commits_after_the_undo_window()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two") });
        var vm = Create();
        vm.DeleteUndoWindow = TimeSpan.FromMilliseconds(500);   // longer than a slow runner's pause
        await vm.RefreshAsync();

        await vm.DeleteAsync(vm.Items[0]);

        Assert.Equal(new long[] { 2 }, vm.Items.Select(item => item.Id));
        Assert.True(vm.HasPendingDelete);
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());

        await WaitUntilAsync(() => !vm.HasPendingDelete, TestContext.Current.CancellationToken);

        await _repository.Received(1).DeleteAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Undo_restores_the_row_in_its_place_and_cancels_the_delete()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two"), Item(3, "three") });
        var vm = Create();
        vm.DeleteUndoWindow = TimeSpan.FromMilliseconds(500);   // undo must land inside it
        await vm.RefreshAsync();
        await vm.DeleteAsync(vm.Items[1]);

        vm.UndoDeleteCommand.Execute(null);
        await Task.Delay(700, TestContext.Current.CancellationToken);   // past the window

        Assert.Equal(new long[] { 1, 2, 3 }, vm.Items.Select(item => item.Id));
        Assert.False(vm.HasPendingDelete);
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refresh_during_the_undo_window_keeps_the_row_hidden()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two") });
        var vm = Create();
        vm.DeleteUndoWindow = TimeSpan.FromSeconds(5);
        await vm.RefreshAsync();
        await vm.DeleteAsync(vm.Items[0]);

        await vm.RefreshAsync();

        Assert.Equal(new long[] { 2 }, vm.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Clearing_everything_drops_pending_undos()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one") });
        var vm = Create();
        vm.DeleteUndoWindow = TimeSpan.FromSeconds(5);
        await vm.RefreshAsync();
        await vm.DeleteAsync(vm.Items[0]);
        Assert.True(vm.HasPendingDelete);

        await vm.ConfirmClearCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingDelete);
    }

    [Fact]
    public void Clear_confirmation_can_be_cancelled()
    {
        var vm = Create();

        vm.RequestClearCommand.Execute(null);
        Assert.True(vm.IsConfirmingClear);

        vm.CancelClearCommand.Execute(null);
        Assert.False(vm.IsConfirmingClear);
    }

    [Fact]
    public void Reset_transient_state_dismisses_a_pending_clear_confirmation()
    {
        var vm = Create();
        vm.RequestClearCommand.Execute(null);

        vm.ResetTransientState();

        Assert.False(vm.IsConfirmingClear);
    }

    [Fact]
    public void Escape_leaves_select_mode_first()
    {
        var vm = Create();
        vm.ToggleSelectionCommand.Execute(null);

        var handled = vm.DismissTransient();

        Assert.True(handled);
        Assert.False(vm.IsSelecting);
    }

    [Fact]
    public void Escape_cancels_a_pending_clear_confirmation()
    {
        var vm = Create();
        vm.RequestClearCommand.Execute(null);

        var handled = vm.DismissTransient();

        Assert.True(handled);
        Assert.False(vm.IsConfirmingClear);
    }

    [Fact]
    public void Escape_with_nothing_to_dismiss_reports_it()
    {
        var vm = Create();

        Assert.False(vm.DismissTransient());
    }

    [Fact]
    public async Task Confirming_clear_removes_unpinned_clips()
    {
        var vm = Create();

        await vm.ConfirmClearCommand.ExecuteAsync(null);

        await _repository.Received(1).ClearUnpinnedAsync(Arg.Any<CancellationToken>());
        Assert.False(vm.IsConfirmingClear);
    }

    [Fact]
    public async Task Clips_are_grouped_into_pinned_and_recent()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "kept", pinned: true), Item(2, "plain") });
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(2);
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("kept", Assert.Single(vm.Groups[0]).Text);
        Assert.Equal("plain", Assert.Single(vm.Groups[1]).Text);
        Assert.Equal("2 clips", vm.CountText);
    }

    [Fact]
    public async Task A_single_clip_is_reported_in_the_singular()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "only") });
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(1);
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal("1 clip", vm.CountText);
    }

    [Fact]
    public async Task A_full_page_offers_more_and_the_footer_shows_the_total()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 50).Select(i => Item(i, $"clip {i}")).ToList());
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(60);
        var vm = Create();

        await vm.RefreshAsync();

        Assert.True(vm.HasMore);
        Assert.Equal("60 clips", vm.CountText);
    }

    [Fact]
    public async Task Loading_more_appends_the_next_page()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 50).Select(i => Item(i, $"clip {i}")).ToList());
        _repository.GetRecentAsync(Arg.Is<PageCursor?>(c => c != null), 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(51, "older") });
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(51);
        var vm = Create();
        await vm.RefreshAsync();

        await vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(51, vm.Items.Count);
        Assert.Equal("older", vm.Items[^1].Text);
        Assert.False(vm.HasMore);
    }

    [Fact]
    public async Task Loading_more_skips_ids_that_are_already_listed()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 50).Select(i => Item(i, $"clip {i}")).ToList());
        _repository.GetRecentAsync(Arg.Is<PageCursor?>(c => c != null), 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(50, "clip 50"), Item(51, "older") });
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(51);
        var vm = Create();
        await vm.RefreshAsync();

        await vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(51, vm.Items.Count);
        Assert.Single(vm.Items, item => item.Text == "clip 50");
    }

    [Fact]
    public async Task An_exhausted_page_offers_no_more()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "only") });
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(1);
        var vm = Create();
        await vm.RefreshAsync();

        Assert.False(vm.HasMore);
        Assert.False(vm.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_load_more_keeps_the_list_and_can_retry()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 50).Select(i => Item(i, $"clip {i}")).ToList());
        _repository.GetRecentAsync(Arg.Is<PageCursor?>(c => c != null), 50, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ClipListItem>>(new IOException("database gone")));
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(60);
        var vm = Create();
        await vm.RefreshAsync();

        await vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(50, vm.Items.Count);
        Assert.True(vm.HasMore);
        Assert.Equal(LoadState.Loaded, vm.State);
    }

    [Fact]
    public async Task Searching_resets_pagination_to_the_first_page()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 50).Select(i => Item(i, $"clip {i}")).ToList());
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(60);
        _repository.SearchAsync("mail", null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(7, "email") });
        _repository.CountAsync("mail", null, null, Arg.Any<CancellationToken>()).Returns(1);
        var vm = Create();
        await vm.RefreshAsync();
        Assert.True(vm.HasMore);

        vm.SearchText = "mail";
        await vm.RefreshAsync();

        Assert.False(vm.HasMore);
        Assert.Equal("email", Assert.Single(vm.Items).Text);
        Assert.Equal("1 clip", vm.CountText);
    }

    [Fact]
    public async Task Empty_history_shows_the_no_clips_placeholder()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>());
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(LoadState.Empty, vm.State);
        Assert.Equal(Tr.Get("Panel_Empty"), vm.EmptyTitle);
        Assert.Equal(Tr.Get("Panel_EmptyHint"), vm.EmptyHint);
    }

    [Fact]
    public async Task Empty_search_shows_the_no_results_placeholder()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>());
        var vm = Create();
        vm.SearchText = "zzz";
        _repository.SearchAsync("zzz", null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>());

        await vm.RefreshAsync();

        Assert.Equal(LoadState.Empty, vm.State);
        Assert.Equal(Tr.Get("Panel_NoResults"), vm.EmptyTitle);
        Assert.Equal(Tr.Get("Panel_NoResultsHint"), vm.EmptyHint);
    }

    [Fact]
    public void Typing_a_search_updates_the_placeholder_text()
    {
        var vm = Create();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.SearchText = "mail";

        Assert.Contains(nameof(ClipboardPanelViewModel.EmptyTitle), changed);
        Assert.Contains(nameof(ClipboardPanelViewModel.EmptyHint), changed);
    }

    [Fact]
    public async Task Search_is_disabled_while_the_history_is_empty()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>());
        var vm = Create();

        await vm.RefreshAsync();

        Assert.False(vm.CanSearch);
    }

    [Fact]
    public async Task Search_stays_enabled_when_a_query_has_no_results()
    {
        var vm = Create();
        vm.SearchText = "zzz";
        _repository.SearchAsync("zzz", null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>());

        await vm.RefreshAsync();

        Assert.True(vm.CanSearch);
    }

    [Fact]
    public async Task Search_enables_when_clips_arrive()
    {
        IReadOnlyList<ClipListItem> results = [];
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(results));
        var vm = Create();
        await vm.RefreshAsync();
        Assert.False(vm.CanSearch);

        results = [Item(1, "one")];
        await vm.RefreshQuietAsync();

        Assert.True(vm.CanSearch);
    }

    [Fact]
    public async Task Copying_selected_clips_joins_them_in_list_order()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(3, "one"), Item(2, "two"), Item(1, "three") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.ToggleSelectionCommand.Execute(null);
        vm.Items[2].IsSelected = true;
        vm.Items[0].IsSelected = true;

        vm.CopySelectedCommand.Execute(null);

        _ = _clipboard.Received(1).WriteText("one\r\nthree");
        Assert.False(vm.IsSelecting);
        Assert.All(vm.Items, item => Assert.False(item.IsSelected));
    }

    [Fact]
    public async Task Clicking_a_row_while_selecting_toggles_it_and_copies_nothing()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.ToggleSelectionCommand.Execute(null);

        vm.Activate(vm.Items[0]);

        Assert.True(vm.Items[0].IsSelected);
        _ = _clipboard.DidNotReceive().WriteText(Arg.Any<string>());
    }

    [Fact]
    public async Task The_selection_count_and_copy_availability_follow_the_checked_rows()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello"), Item(2, "world") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.ToggleSelectionCommand.Execute(null);
        Assert.False(vm.CanCopySelection);

        vm.Items[1].IsSelected = true;

        Assert.True(vm.CanCopySelection);
        Assert.Equal("1 selected", vm.FooterText);

        vm.Items[0].IsSelected = true;

        Assert.Equal("2 selected", vm.FooterText);
    }

    [Fact]
    public async Task Leaving_selection_clears_the_checked_rows()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.ToggleSelectionCommand.Execute(null);
        vm.Items[0].IsSelected = true;

        vm.ToggleSelectionCommand.Execute(null);

        Assert.False(vm.Items[0].IsSelected);
        Assert.Equal(vm.CountText, vm.FooterText);
    }

    [Fact]
    public async Task Copying_selected_clips_signals_the_window()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.ToggleSelectionCommand.Execute(null);
        vm.Items[0].IsSelected = true;
        var signalled = false;
        vm.SelectionCopied += (_, _) => signalled = true;

        vm.CopySelectedCommand.Execute(null);

        Assert.True(signalled);
    }

    [Fact]
    public async Task Copying_selected_clips_shows_the_copy_notice()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.ToggleSelectionCommand.Execute(null);
        vm.Items[0].IsSelected = true;

        vm.CopySelectedCommand.Execute(null);

        Assert.True(vm.HasCopyNotice);
    }

    [Fact]
    public void Reset_transient_state_leaves_select_mode()
    {
        var vm = Create();
        vm.ToggleSelectionCommand.Execute(null);

        vm.ResetTransientState();

        Assert.False(vm.IsSelecting);
    }

    [Fact]
    public async Task Assigning_a_category_calls_the_repository()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();

        await vm.AssignCategoryAsync(vm.Items[0], 7);

        await _repository.Received(1).AssignCategoryAsync(1, 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creating_a_category_trims_the_name()
    {
        var vm = Create();

        await vm.GetOrAddCategoryAsync("  Work  ");

        await _categories.Received(1).GetOrAddAsync("Work", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Requesting_the_category_menu_hands_over_the_row()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();
        ClipItemViewModel? requested = null;
        vm.CategoryMenuRequested += item => requested = item;

        vm.RequestCategoryMenu(vm.Items[0]);

        Assert.Same(vm.Items[0], requested);
    }

    [Fact]
    public async Task Loading_the_filter_lists_all_categories_plus_all_clips()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work") });
        var vm = Create();

        await vm.RefreshCategoryFilterAsync();

        Assert.Equal(2, vm.CategoryFilterOptions.Count);
        Assert.Null(vm.CategoryFilterOptions[0].Value);
        Assert.Equal(7, vm.CategoryFilterOptions[1].Value);
        Assert.Equal("Work", vm.CategoryFilterOptions[1].Label);
    }

    [Fact]
    public async Task Picking_a_category_filter_narrows_the_query()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work") });
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7)
            .Returns(new List<ClipListItem> { Item(1, "saved") });
        var vm = Create();
        await vm.RefreshCategoryFilterAsync();

        vm.SelectedCategoryFilter = vm.CategoryFilterOptions[1];
        await vm.RefreshAsync();

        Assert.Equal("saved", Assert.Single(vm.Items).Text);
        await _repository.Received().GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7);
    }

    [Fact]
    public async Task An_empty_filtered_view_explains_itself()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work") });
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7)
            .Returns(new List<ClipListItem>());
        var vm = Create();
        await vm.RefreshCategoryFilterAsync();
        vm.SelectedCategoryFilter = vm.CategoryFilterOptions[1];

        await vm.RefreshAsync();

        Assert.Equal(LoadState.Empty, vm.State);
        Assert.Equal(Tr.Get("Panel_EmptyCategory"), vm.EmptyTitle);
    }

    [Fact]
    public async Task Unlocked_categories_are_asked_for_in_the_query()
    {
        _locks.Unlock(7);
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), null,
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(7)))
            .Returns(new List<ClipListItem> { Item(1, "secret") });
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal("secret", Assert.Single(vm.Items).Text);
    }

    [Fact]
    public async Task Locked_categories_are_marked_and_explained_in_the_filter()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work", true) });
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7, null)
            .Returns(new List<ClipListItem>());
        var vm = Create();

        await vm.RefreshCategoryFilterAsync();
        Assert.Equal(Tr.Format("History_FilterLocked", "Work"), vm.CategoryFilterOptions[1].Label);

        vm.SelectedCategoryFilter = vm.CategoryFilterOptions[1];
        await vm.RefreshAsync();

        Assert.Equal(LoadState.Empty, vm.State);
        Assert.Equal(Tr.Get("Panel_EmptyLocked"), vm.EmptyTitle);
    }

    [Fact]
    public async Task Unlocking_a_locked_category_shows_its_clips()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work", true) });
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7, null)
            .Returns(new List<ClipListItem>());
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7,
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(7)))
            .Returns(new List<ClipListItem> { Item(1, "secret") });
        var vm = Create();
        await vm.RefreshCategoryFilterAsync();
        vm.SelectedCategoryFilter = vm.CategoryFilterOptions[1];

        await vm.UnlockCategoryAsync(7);

        Assert.Contains(7, _locks.UnlockedCategoryIds);
        Assert.Equal("Work", vm.CategoryFilterOptions[1].Label);
        Assert.Equal("secret", Assert.Single(vm.Items).Text);
    }

    [Fact]
    public async Task A_lock_change_marks_the_filter_and_keeps_the_selection()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work", true) });
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7, null)
            .Returns(new List<ClipListItem>());
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7,
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(7)))
            .Returns(new List<ClipListItem>());
        var vm = Create();
        _locks.Unlock(7);
        await vm.RefreshCategoryFilterAsync();
        vm.SelectedCategoryFilter = vm.CategoryFilterOptions[1];

        _locks.Reset();                      // what lock-on-minimize does
        await vm.RefreshQuietAsync();

        Assert.Equal(7, vm.SelectedCategoryFilter?.Value);
        Assert.True(vm.IsCategoryFilterLocked);
        Assert.Equal(Tr.Format("History_FilterLocked", "Work"), vm.CategoryFilterOptions[1].Label);
        Assert.Equal(Tr.Get("Panel_EmptyLocked"), vm.EmptyTitle);
    }

    [Fact]
    public async Task Refreshing_the_filter_never_reloads_the_whole_list()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work", true) });
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>(), 7,
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(7)))
            .Returns(new List<ClipListItem> { Item(1, "saved") });
        var vm = Create();
        _locks.Unlock(7);
        await vm.RefreshCategoryFilterAsync();
        vm.SelectedCategoryFilter = vm.CategoryFilterOptions[1];
        await vm.RefreshAsync();

        await vm.RefreshCategoryFilterAsync();

        await _repository.DidNotReceive().GetRecentAsync(
            null, 50, Arg.Any<CancellationToken>(), null, null);
        Assert.Equal(7, vm.SelectedCategoryFilter?.Value);
        Assert.Equal("saved", Assert.Single(vm.Items).Text);
    }

    [Fact]
    public async Task Arrow_navigation_moves_the_selection()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two"), Item(3, "three") });
        var vm = Create();
        await vm.RefreshAsync();

        vm.MoveSelection(1);
        Assert.Equal("one", vm.SelectedItem?.Text);
        vm.MoveSelection(1);
        Assert.Equal("two", vm.SelectedItem?.Text);
        vm.MoveSelection(-1);
        Assert.Equal("one", vm.SelectedItem?.Text);
        vm.MoveSelection(-1);
        Assert.Equal("one", vm.SelectedItem?.Text);
    }

    [Fact]
    public async Task Enter_activates_the_selected_clip()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two") });
        var vm = Create();
        await vm.RefreshAsync();
        var signalled = false;
        vm.ItemActivated += (_, _) => signalled = true;
        vm.MoveSelection(1);
        vm.MoveSelection(1);

        vm.ActivateSelected();

        _ = _clipboard.Received(1).WriteText("two");
        Assert.True(signalled);
    }

    [Fact]
    public async Task Enter_without_a_selection_takes_the_first_clip()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one") });
        var vm = Create();
        await vm.RefreshAsync();

        vm.ActivateSelected();

        _ = _clipboard.Received(1).WriteText("one");
    }

    [Fact]
    public async Task Activating_a_clip_shows_a_copy_notice()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();

        vm.Activate(vm.Items[0]);

        Assert.True(vm.HasCopyNotice);
        Assert.NotNull(vm.CopyNotice);
        Assert.Equal(vm.CopyNotice, vm.FooterText);
    }

    [Fact]
    public async Task The_copy_notice_color_flag_follows_the_footer_text()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();

        vm.Activate(vm.Items[0]);
        Assert.True(vm.IsShowingCopyNotice);
        Assert.False(vm.ShowFooterText);

        vm.ToggleSelectionCommand.Execute(null);
        Assert.False(vm.IsShowingCopyNotice);
        Assert.True(vm.ShowFooterText);

        vm.ToggleSelectionCommand.Execute(null);
        Assert.True(vm.IsShowingCopyNotice);
        Assert.False(vm.ShowFooterText);
    }

    [Fact]
    public async Task The_copy_notice_clears_after_its_window()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        vm.CopyNoticeDuration = TimeSpan.FromMilliseconds(30);
        await vm.RefreshAsync();

        vm.Activate(vm.Items[0]);
        await WaitUntilAsync(() => !vm.HasCopyNotice, TestContext.Current.CancellationToken);

        Assert.False(vm.HasCopyNotice);
        Assert.Equal(vm.CountText, vm.FooterText);
    }

    [Fact]
    public async Task Activating_an_image_clip_shows_the_copy_notice()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>
            {
                new(1, string.Empty, "Snipping Tool", false,
                    new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), new PageCursor("k", 1), null, null, [1, 2, 3]),
            });
        _repository.GetImageAsync(1, Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        var vm = Create();
        await vm.RefreshAsync();

        vm.Activate(vm.Items[0]);

        Assert.True(vm.HasCopyNotice);
    }

    [Fact]
    public async Task Reset_transient_state_clears_the_copy_notice()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "hello") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.Activate(vm.Items[0]);
        Assert.True(vm.HasCopyNotice);

        vm.ResetTransientState();

        Assert.False(vm.HasCopyNotice);
    }

    [Fact]
    public async Task Opening_the_flyout_clears_the_keyboard_selection()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.MoveSelection(1);
        Assert.NotNull(vm.SelectedItem);

        vm.ResetTransientState();

        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public async Task Activating_an_image_clip_writes_the_image()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>
            {
                new(1, string.Empty, "Snipping Tool", false,
                    new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), new PageCursor("k", 1), null, null, [1, 2, 3]),
            });
        _repository.GetImageAsync(1, Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        var vm = Create();
        await vm.RefreshAsync();
        var signalled = false;
        vm.ItemActivated += (_, _) => signalled = true;

        vm.Activate(vm.Items[0]);

        await _clipboard.Received(1).WriteImageAsync(Arg.Is<byte[]>(bytes => bytes.Length == 3));
        _ = _clipboard.DidNotReceive().WriteText(Arg.Any<string>());
        Assert.True(signalled);
    }

    [Fact]
    public async Task Copying_selected_clips_skips_images()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem>
            {
                Item(1, "one"),
                new(2, string.Empty, "Snipping Tool", false,
                    new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), new PageCursor("k", 2), null, null, [1, 2, 3]),
            });
        var vm = Create();
        await vm.RefreshAsync();
        vm.ToggleSelectionCommand.Execute(null);
        vm.Items[0].IsSelected = true;
        vm.Items[1].IsSelected = true;

        vm.CopySelectedCommand.Execute(null);

        _ = _clipboard.Received(1).WriteText("one");
    }

    [Fact]
    public async Task The_last_visible_row_follows_the_groups_not_the_raw_order()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(2, "recent"), Item(1, "pinned-oldest", pinned: true) });
        var vm = Create();
        await vm.RefreshAsync();

        Assert.Equal("pinned-oldest", vm.Items[^1].Text);   // raw order ends on the pinned row
        Assert.Equal("recent", vm.LastVisibleItem?.Text);   // the visible bottom is Recent's last row
    }

    [Fact]
    public async Task Clearing_everything_still_commits_a_pending_delete_of_a_pinned_clip()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "kept", pinned: true) });
        var vm = Create();
        vm.DeleteUndoWindow = TimeSpan.FromSeconds(5);
        await vm.RefreshAsync();
        await vm.DeleteAsync(vm.Items[0]);
        Assert.True(vm.HasPendingDelete);

        await vm.ConfirmClearCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingDelete);
        await _repository.Received(1).DeleteAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pending_delete_drops_out_of_the_footer_count()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one"), Item(2, "two") });
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(2);
        var vm = Create();
        vm.DeleteUndoWindow = TimeSpan.FromSeconds(5);
        await vm.RefreshAsync();
        Assert.Equal("2 clips", vm.CountText);

        await vm.DeleteAsync(vm.Items[0]);

        Assert.Equal("1 clip", vm.CountText);
    }

    [Fact]
    public async Task A_quiet_refresh_keeps_the_pages_already_loaded()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 50).Select(i => Item(i, $"clip {i}")).ToList());
        _repository.GetRecentAsync(Arg.Is<PageCursor?>(c => c != null), 50, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(51, 10).Select(i => Item(i, $"clip {i}")).ToList());
        _repository.CountAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(60);
        var vm = Create();
        await vm.RefreshAsync();
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(60, vm.Items.Count);

        await vm.RefreshQuietAsync();

        Assert.Equal(60, vm.Items.Count);
        Assert.Equal("clip 1", vm.Items[0].Text);
    }

    [Fact]
    public async Task A_hidden_view_skips_background_refreshes()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one") });
        var vm = Create();
        await vm.RefreshAsync();
        vm.IsViewVisible = false;
        _repository.ClearReceivedCalls();

        WeakReferenceMessenger.Default.Send(new DataChanged("clip"));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await _repository.DidNotReceive().GetRecentAsync(null, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_visible_view_refreshes_on_changes()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one") });
        var vm = Create();
        await vm.RefreshAsync();
        _repository.ClearReceivedCalls();

        WeakReferenceMessenger.Default.Send(new DataChanged("clip"));
        await WaitUntilAsync(
            () => _repository.ReceivedCalls().Any(), TestContext.Current.CancellationToken);

        await _repository.Received().GetRecentAsync(null, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relock_drops_hidden_rows()
    {
        var vm = await LoadWithUnlockedSecretAsync();
        vm.SelectedItem = vm.Items.Single(item => item.CategoryId == 5);
        vm.IsViewVisible = false;     // a hidden flyout skips reloads; its rows must still go
        _repository.ClearReceivedCalls();

        _locks.Lock(5);
        WeakReferenceMessenger.Default.Send(new DataChanged("lock"));

        await _repository.DidNotReceiveWithAnyArgs().GetRecentAsync(default, default, TestContext.Current.CancellationToken);
        Assert.Equal(["open"], vm.Items.Select(item => item.Text));
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public async Task Hidden_row_not_picked()
    {
        var vm = await LoadWithUnlockedSecretAsync();
        var secret = vm.Items.Single(item => item.CategoryId == 5);
        var signalled = false;
        vm.ItemActivated += (_, _) => signalled = true;

        _locks.Lock(5);
        vm.Activate(secret);

        _ = _clipboard.DidNotReceive().WriteText(Arg.Any<string>());
        Assert.False(signalled);
    }

    [Fact]
    public async Task Failed_copy_does_not_paste()
    {
        _repository.GetRecentAsync(null, 50, Arg.Any<CancellationToken>())
            .Returns(new List<ClipListItem> { Item(1, "one") });
        _clipboard.WriteText("one").Returns(false);
        var vm = Create();
        await vm.RefreshAsync();
        var signalled = false;
        vm.ItemActivated += (_, _) => signalled = true;

        vm.Activate(vm.Items[0]);

        Assert.False(signalled);
        Assert.Equal(Tr.Get("Clip_CopyFailed"), vm.CopyNotice);
    }

    // Category 5 is locked but unlocked for now; the list shows one clip in it and one outside.
    // The store hides the locked clip unless the query passes 5 as unlocked, like the real one.
    private async Task<ClipboardPanelViewModel> LoadWithUnlockedSecretAsync()
    {
        _categories.GetCategoriesAsync(default).ReturnsForAnyArgs(new List<Category> { new(5, "Secret", IsLocked: true) });
        _repository.GetRecentAsync(default, default, default).ReturnsForAnyArgs(call =>
            call.ArgAt<IReadOnlyCollection<long>?>(4)?.Contains(5) == true
                ? [Item(1, "open"), Item(2, "secret") with { CategoryId = 5 }]
                : new List<ClipListItem> { Item(1, "open") });
        _locks.Unlock(5);
        var vm = Create();
        await vm.RefreshCategoryFilterAsync();
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Items.Count);
        return vm;
    }

    private ClipboardPanelViewModel Create() =>
        new(_repository, _categories, _locks, _passwords, _clipboard, _dates,
            NullLogger<ClipboardPanelViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20, ct);
    }

    private static ClipListItem Item(long id, string text, bool pinned = false) =>
        new(id, text, "Notepad", pinned, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), new PageCursor("k", id));
}
