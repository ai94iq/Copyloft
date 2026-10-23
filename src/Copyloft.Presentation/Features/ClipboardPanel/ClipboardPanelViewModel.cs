using System.Collections.ObjectModel;
using Copyloft.Core.Abstractions;

namespace Copyloft.Presentation.Features.ClipboardPanel;

// Drives the floating history list: load, search, copy, pin, delete and clear.
public sealed partial class ClipboardPanelViewModel : PageViewModel, IIncrementalSource
{
    private const int PageSize = 50;
    private const int SearchDebounceMs = 300;

    private readonly IClipRepository _repository;
    private readonly ICategoryRepository _categories;
    private readonly ICategoryLockService _locks;
    private readonly ILockPasswordService _passwords;
    private readonly IClipboardWriter _clipboard;
    private readonly IDateFormatter _dates;
    private readonly HashSet<long> _lockedCategoryIds = [];
    private readonly HashSet<long> _lockableCategoryIds = [];
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _noticeCts;
    private readonly List<PendingDelete> _pendingDeletes = [];
    private bool _quietRefreshInProgress;
    private bool _quietRefreshRequested;
    private PageCursor? _cursor;
    private int _loadGeneration;

    public ClipboardPanelViewModel(
        IClipRepository repository,
        ICategoryRepository categories,
        ICategoryLockService locks,
        ILockPasswordService passwords,
        IClipboardWriter clipboard,
        IDateFormatter dates,
        ILogger<ClipboardPanelViewModel> log) : base(log)
    {
        _repository = repository;
        _categories = categories;
        _locks = locks;
        _passwords = passwords;
        _clipboard = clipboard;
        _dates = dates;
        ReloadQuietlyOnChange("clip", "lock");
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(CanSearch));
            OnPropertyChanged(nameof(FooterText));
        };
    }

    public ObservableCollection<ClipItemViewModel> Items { get; } = [];

    // Grouped view (Pinned, then Recent) used by the full history page.
    public ObservableCollection<ClipGroup> Groups { get; } = [];

    // The bottom row on screen: the end of the last group, which is what the views watch to load
    // the next page. Items' raw order is recency order, not the display order.
    public ClipItemViewModel? LastVisibleItem => Groups.Count == 0 ? null : Groups[^1].LastOrDefault();

    public bool HasItems => Items.Count > 0;

    // The total the current query can show, not just the loaded page, so the footer tells the truth.
    // Rows waiting on undo are already hidden from the list, so they leave the count too.
    public string CountText => Tr.Plural("History_Count", Math.Max(0, TotalCount - _pendingDeletes.Count));

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    // Paging: the view asks for the next page near the end of the scroll (IIncrementalSource).
    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    // The full-history category filter: "All categories" plus one entry per category.
    public ObservableCollection<Option<long?>> CategoryFilterOptions { get; } = [];

    [ObservableProperty]
    public partial Option<long?>? SelectedCategoryFilter { get; set; }

    private bool _updatingFilter;

    public bool IsCategoryFilterActive => SelectedCategoryFilter?.Value is not null;

    public bool IsCategoryFilterLocked =>
        SelectedCategoryFilter?.Value is long id && _lockedCategoryIds.Contains(id);

    public async Task RefreshCategoryFilterAsync()
    {
        var categories = await _categories.GetCategoriesAsync(CancellationToken.None);
        var previous = SelectedCategoryFilter?.Value;

        // Rebuilding the list resets the combo's selection for a moment; guard the whole rebuild
        // so that never triggers a refresh without the filter.
        _updatingFilter = true;
        try
        {
            _lockedCategoryIds.Clear();
            _lockableCategoryIds.Clear();
            CategoryFilterOptions.Clear();
            CategoryFilterOptions.Add(new Option<long?>(null, Tr.Get("History_FilterAll")));
            foreach (var category in categories)
            {
                if (category.IsLocked) _lockableCategoryIds.Add(category.Id);
                var locked = category.IsLocked && !_locks.UnlockedCategoryIds.Contains(category.Id);
                if (locked) _lockedCategoryIds.Add(category.Id);
                CategoryFilterOptions.Add(new Option<long?>(
                    category.Id, locked ? Tr.Format("History_FilterLocked", category.Name) : category.Name));
            }

            var match = CategoryFilterOptions.FirstOrDefault(option => option.Value == previous)
                ?? CategoryFilterOptions[0];
            if (!Equals(match, SelectedCategoryFilter)) SelectedCategoryFilter = match;
        }
        finally
        {
            _updatingFilter = false;
        }
    }

    public async Task UnlockCategoryAsync(long id)
    {
        _locks.Unlock(id);
        await RefreshCategoryFilterAsync();
        await RefreshAsync();
    }

    public bool VerifyLockPassword(string password) => _passwords.Verify(password);

    // Rows on screen may predate a relock, and hidden views skip reloads: a lock change drops
    // the rows it hides at once, and a category change (which can lock one) re-reads the flags first.
    protected override void OnDataChanged(DataChanged change)
    {
        if (change.Area == "lock") DropLockedRows();
        else if (change.Area == "category") _ = RefreshLockedCategoriesAsync();
    }

    private async Task RefreshLockedCategoriesAsync()
    {
        try
        {
            await RefreshCategoryFilterAsync();
            DropLockedRows();
        }
        catch (Exception ex)
        {
            LogQuietFailure(ex);
        }
    }

    private bool IsHiddenByLock(ClipItemViewModel item) =>
        item.CategoryId is long id && _lockableCategoryIds.Contains(id) && !_locks.UnlockedCategoryIds.Contains(id);

    private void DropLockedRows()
    {
        var hidden = Items.Where(IsHiddenByLock).ToList();
        if (hidden.Count == 0) return;

        foreach (var item in hidden) Items.Remove(item);
        if (SelectedItem is { } selected && hidden.Contains(selected)) SelectedItem = null;
        RebuildGroups();
    }

    // The unlock set for repository calls; null when nothing is unlocked.
    private IReadOnlyCollection<long>? Unlocked =>
        _locks.UnlockedCategoryIds.Count == 0 ? null : _locks.UnlockedCategoryIds;

    partial void OnSelectedCategoryFilterChanged(Option<long?>? value)
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        if (_updatingFilter) return;

        _ = RefreshAsync();
    }

    // Multi-select: rows are checked, then copied together in one go.
    public int SelectedCount => Items.Count(item => item.IsSelected);

    public string SelectionCountText => Tr.Format("Selection_Count", SelectedCount);

    public bool CanCopySelection => SelectedCount > 0;

    // While a copy confirmation is showing, the footer shows it instead of the count.
    public string FooterText => IsSelecting ? SelectionCountText : CopyNotice ?? CountText;

    public bool ShowSelectButton => !IsSelecting;

    // Placeholder copy: an empty history, an empty search and an empty filter read differently.
    public string EmptyTitle =>
        IsSearchActive ? Tr.Get("Panel_NoResults")
        : IsCategoryFilterLocked ? Tr.Get("Panel_EmptyLocked")
        : IsCategoryFilterActive ? Tr.Get("Panel_EmptyCategory")
        : Tr.Get("Panel_Empty");

    public string EmptyHint =>
        IsSearchActive ? Tr.Get("Panel_NoResultsHint")
        : IsCategoryFilterLocked ? Tr.Get("Panel_EmptyLockedHint")
        : IsCategoryFilterActive ? Tr.Get("Panel_EmptyCategoryHint")
        : Tr.Get("Panel_EmptyHint");

    // No clips means there is nothing to search; a typed query keeps the box usable so it can be edited.
    public bool CanSearch => Items.Count > 0 || IsSearchActive;

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConfirmingClear { get; set; }

    [ObservableProperty]
    public partial bool IsSelecting { get; set; }

    // Transient "Copied" feedback in the footer; the flyout hides on pick, so only History shows it.
    [ObservableProperty]
    public partial string? CopyNotice { get; set; }

    public bool HasCopyNotice => CopyNotice is not null;

    // The footer shows the notice (and its success color) only while the count is not replaced
    // by selection mode.
    public bool IsShowingCopyNotice => !IsSelecting && HasCopyNotice;

    // The summary (count or selection text) hides while the notice takes the footer over.
    public bool ShowFooterText => !IsShowingCopyNotice;

    // Tests shorten this; the UI keeps it long enough to read.
    public TimeSpan CopyNoticeDuration { get; set; } = TimeSpan.FromSeconds(2.5);

    // Raised when the user picks an item; the window hides and optionally pastes.
    public event EventHandler? ItemActivated;

    // Raised after the selected clips were copied; the flyout hides.
    public event EventHandler? SelectionCopied;

    // Raised when the user asks for the full history window.
    public event EventHandler? OpenHistoryRequested;

    [RelayCommand]
    private void OpenHistory() => OpenHistoryRequested?.Invoke(this, EventArgs.Empty);

    // Raised when a row asks for its category menu; the window shows it next to the row.
    public event Action<ClipItemViewModel>? CategoryMenuRequested;

    public void RequestCategoryMenu(ClipItemViewModel item) => CategoryMenuRequested?.Invoke(item);

    // Assignment comes back as a "clip" change, so open lists refresh themselves.
    public Task AssignCategoryAsync(ClipItemViewModel item, long? categoryId) =>
        _repository.AssignCategoryAsync(item.Id, categoryId, CancellationToken.None, Unlocked);

    public Task<IReadOnlyList<Category>> GetCategoriesAsync() =>
        _categories.GetCategoriesAsync(CancellationToken.None);

    public Task<Category> GetOrAddCategoryAsync(string name) =>
        _categories.GetOrAddAsync(name.Trim(), CancellationToken.None);

    public Task RefreshAsync() => ReloadCommand.ExecuteAsync(null);

    public void ResetTransientState()
    {
        IsConfirmingClear = false;
        IsSelecting = false;
        SelectedItem = null;
        _noticeCts?.Cancel();
        CopyNotice = null;
    }

    // Esc: leave select mode, then cancel the clear confirmation. False = nothing left to dismiss.
    public bool DismissTransient()
    {
        if (IsSelecting)
        {
            ToggleSelectionCommand.Execute(null);
            return true;
        }

        if (IsConfirmingClear)
        {
            CancelClearCommand.Execute(null);
            return true;
        }

        return false;
    }

    private void ShowCopyNotice() => ShowNotice(Tr.Get("History_Copied"));

    private void ShowNotice(string text)
    {
        _noticeCts?.Cancel();
        var cts = _noticeCts = new CancellationTokenSource();
        CopyNotice = text;
        _ = ClearCopyNoticeAsync(cts.Token);
    }

    private async Task ClearCopyNoticeAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(CopyNoticeDuration, ct);
            CopyNotice = null;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer copy or a reset
        }
    }

    partial void OnCopyNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCopyNotice));
        OnPropertyChanged(nameof(IsShowingCopyNotice));
        OnPropertyChanged(nameof(ShowFooterText));
        OnPropertyChanged(nameof(FooterText));
    }

    // Keyboard navigation: the flyout moves a selection with the arrow keys and pastes with Enter.
    [ObservableProperty]
    public partial ClipItemViewModel? SelectedItem { get; set; }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;

        var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        if (index < 0)
        {
            SelectedItem = Items[delta > 0 ? 0 : Items.Count - 1];
            return;
        }

        SelectedItem = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    public void ActivateSelected()
    {
        var item = SelectedItem ?? Items.FirstOrDefault();
        if (item is not null) Activate(item);
    }

    // Quiet refresh for opening the flyout: keeps the previous list on screen while the
    // fresh query runs, and skips repainting entirely when nothing changed since last open.
    public async Task RefreshQuietAsync()
    {
        _quietRefreshRequested = true;
        if (_quietRefreshInProgress) return;

        _quietRefreshInProgress = true;
        try
        {
            while (_quietRefreshRequested)
            {
                _quietRefreshRequested = false;
                try
                {
                    // Re-read as deep as the user already scrolled, so a quiet refresh does not
                    // collapse the list back to the first page.
                    var loaded = Items.Count;
                    var generation = _loadGeneration;
                    var first = await FetchPageAsync(null, CancellationToken.None);
                    var all = first.ToList();
                    var cursor = first.Count > 0 ? first[^1].Cursor : null;
                    var hasMore = first.Count == PageSize;
                    while (hasMore && all.Count < loaded && cursor is not null)
                    {
                        var page = await FetchPageAsync(cursor, CancellationToken.None);
                        all.AddRange(page);
                        cursor = page.Count > 0 ? page[^1].Cursor : null;
                        hasMore = page.Count == PageSize;
                    }

                    var total = await CountAsync(CancellationToken.None);

                    // A newer load replaced the list meanwhile; this older result may predate a relock.
                    if (generation != _loadGeneration) continue;

                    ApplyFirstPage(all, total, hasMore);
                    MarkQuietLoadCompleted(Items.Count > 0);
                }
                catch (Exception ex)
                {
                    // Keep whatever is already on screen; don't flash an error over a quick refresh.
                    MarkQuietLoadFailed(ex);
                }
            }
        }
        finally
        {
            _quietRefreshInProgress = false;
        }

        // A lock change also moves categories between locked and unlocked, which the filter shows.
        await RefreshCategoryFilterAsync();
    }

    protected override Task ReloadQuietlyAsync() => RefreshQuietAsync();

    public void Activate(ClipItemViewModel item)
    {
        if (IsSelecting)
        {
            item.IsSelected = !item.IsSelected;
            return;
        }

        // A row left over from before a relock is not copied.
        if (IsHiddenByLock(item)) return;

        // Image clips load their full image before it goes back to the clipboard.
        _ = item.HasImage ? ActivateImageAsync(item) : ActivateTextAsync(item);
    }

    private async Task ActivateTextAsync(ClipItemViewModel item) =>
        CompleteActivation(await _clipboard.WriteText(item.Text));

    private async Task ActivateImageAsync(ClipItemViewModel item)
    {
        var image = await _repository.GetImageAsync(item.Id, CancellationToken.None, Unlocked);
        if (image is null) return;

        CompleteActivation(await _clipboard.WriteImageAsync(image));
    }

    // Paste-back follows only a write that landed; otherwise it would paste whatever was there before.
    private void CompleteActivation(bool written)
    {
        if (!written)
        {
            ShowNotice(Tr.Get("Clip_CopyFailed"));
            return;
        }

        ShowCopyNotice();
        ItemActivated?.Invoke(this, EventArgs.Empty);
    }

    public Task TogglePinAsync(ClipItemViewModel item) =>
        _repository.SetPinnedAsync(item.Id, !item.IsPinned, CancellationToken.None, Unlocked);

    // Delete is deferred: the row disappears at once and the database delete runs when the
    // undo window closes, so a misclick never loses a clip.
    public TimeSpan DeleteUndoWindow { get; set; } = TimeSpan.FromSeconds(6);

    public bool HasPendingDelete => _pendingDeletes.Count > 0;

    public string PendingDeleteText => Tr.Get("Clip_Deleted");

    public Task DeleteAsync(ClipItemViewModel item)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return Task.CompletedTask;

        var cts = new CancellationTokenSource();
        _pendingDeletes.Add(new PendingDelete(item.Id, item, index, cts));
        Items.RemoveAt(index);
        RebuildGroups();
        NotifyPendingDeleteChanged();
        _ = CommitDeleteAsync(item.Id, cts.Token);
        return Task.CompletedTask;
    }

    private async Task CommitDeleteAsync(long id, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DeleteUndoWindow, ct);
        }
        catch (OperationCanceledException)
        {
            return;                                   // undone before the window closed
        }

        var pending = _pendingDeletes.FirstOrDefault(entry => entry.Id == id);
        if (pending is null) return;

        _pendingDeletes.Remove(pending);
        NotifyPendingDeleteChanged();
        try
        {
            await _repository.DeleteAsync(id, CancellationToken.None, Unlocked);
        }
        catch (Exception ex)
        {
            // The row is already hidden; the next refresh brings it back if the delete failed.
            LogQuietFailure(ex);
        }
    }

    [RelayCommand]
    private void UndoDelete()
    {
        if (_pendingDeletes.Count == 0) return;

        var pending = _pendingDeletes[^1];
        _pendingDeletes.RemoveAt(_pendingDeletes.Count - 1);
        pending.Cts.Cancel();
        Items.Insert(Math.Min(pending.Index, Items.Count), pending.Item);
        RebuildGroups();
        NotifyPendingDeleteChanged();
    }

    // Clearing the history also drops pending deletes; their rows are gone, undo must not revive them.
    private void ClearPendingDeletes()
    {
        if (_pendingDeletes.Count == 0) return;

        foreach (var pending in _pendingDeletes) pending.Cts.Cancel();
        _pendingDeletes.Clear();
        NotifyPendingDeleteChanged();
    }

    private void NotifyPendingDeleteChanged()
    {
        OnPropertyChanged(nameof(HasPendingDelete));
        OnPropertyChanged(nameof(PendingDeleteText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(FooterText));
    }

    private sealed record PendingDelete(
        long Id, ClipItemViewModel Item, int Index, CancellationTokenSource Cts);

    protected override async Task<bool> LoadCoreAsync(CancellationToken ct)
    {
        var generation = _loadGeneration;
        var (items, total) = await LoadItemsAsync(ct);

        // A newer load already replaced the list; this older result may predate a relock.
        if (generation != _loadGeneration) return Items.Count > 0;

        ApplyFirstPage(items, total, items.Count == PageSize);
        IsConfirmingClear = false;
        return Items.Count > 0;
    }

    private async Task<(IReadOnlyList<ClipListItem> Items, int Total)> LoadItemsAsync(CancellationToken ct) =>
        (await FetchPageAsync(null, ct), await CountAsync(ct));

    // One page of the current query at the given cursor.
    private async Task<IReadOnlyList<ClipListItem>> FetchPageAsync(PageCursor? cursor, CancellationToken ct)
    {
        var categoryId = SelectedCategoryFilter?.Value;
        return string.IsNullOrWhiteSpace(SearchText)
            ? await _repository.GetRecentAsync(cursor, PageSize, ct, categoryId, Unlocked)
            : await _repository.SearchAsync(SearchText, cursor, PageSize, ct, categoryId, Unlocked);
    }

    // The total for the current query, matching FetchPageAsync's filters.
    private Task<int> CountAsync(CancellationToken ct)
    {
        var categoryId = SelectedCategoryFilter?.Value;
        var query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;
        return _repository.CountAsync(query, categoryId, Unlocked, ct);
    }

    // First page of a fresh query: replaces the list and resets paging.
    private void ApplyFirstPage(IReadOnlyList<ClipListItem> items, int total, bool hasMore)
    {
        _loadGeneration++;
        _cursor = items.Count > 0 ? items[^1].Cursor : null;
        HasMore = hasMore;
        TotalCount = total;
        ReplaceItems(items);
    }

    // Next page: appends only rows that are not already listed, then rebuilds the groups.
    private void AppendItems(IReadOnlyList<ClipListItem> page)
    {
        if (page.Count == 0) return;

        var known = Items.Select(item => item.Id)
            .Concat(_pendingDeletes.Select(entry => entry.Id))
            .ToHashSet();
        foreach (var model in page)
            if (known.Add(model.Id)) Items.Add(new ClipItemViewModel(model, _dates, this));
        RebuildGroups();
    }

    private bool CanLoadMore => HasMore && !IsLoadingMore && _cursor is not null;

    // Called by the view near the end of the scroll (IIncrementalSource).
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_cursor is null) return;

        IsLoadingMore = true;
        try
        {
            var generation = _loadGeneration;
            var page = await FetchPageAsync(_cursor, ct);

            // A refresh or a new search replaced the list while this page was loading.
            if (generation != _loadGeneration) return;

            AppendItems(page);
            if (page.Count > 0) _cursor = page[^1].Cursor;
            HasMore = page.Count == PageSize;
        }
        catch (OperationCanceledException)
        {
            // cancelled; keep the page that is on screen
        }
        catch (Exception ex)
        {
            // Keep what is on screen; the next scroll retries.
            LogQuietFailure(ex);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(FooterText));
    }

    private void ReplaceItems(IEnumerable<ClipListItem> items)
    {
        var existing = Items.ToDictionary(item => item.Id);
        var desired = new List<ClipItemViewModel>();
        foreach (var model in items)
        {
            if (_pendingDeletes.Any(entry => entry.Id == model.Id)) continue;   // still waiting on undo

            if (existing.TryGetValue(model.Id, out var item))
            {
                item.UpdateFrom(model, _dates);
                desired.Add(item);
            }
            else
            {
                desired.Add(new ClipItemViewModel(model, _dates, this));
            }
        }

        ReconcileItems(Items, desired);
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        var desired = new List<(string TitleKey, ClipItemViewModel[] Items)>();
        AddGroup(desired, "History_Pinned", Items.Where(i => i.IsPinned));
        AddGroup(desired, "History_Recent", Items.Where(i => !i.IsPinned));

        var groups = new List<ClipGroup>();
        foreach (var (titleKey, items) in desired)
        {
            var title = Tr.Get(titleKey);
            var group = Groups.FirstOrDefault(candidate => candidate.Title == title) ?? new ClipGroup(title);
            ReconcileItems(group, items);
            groups.Add(group);
        }

        for (var index = Groups.Count - 1; index >= 0; index--)
            if (!groups.Contains(Groups[index])) Groups.RemoveAt(index);

        for (var index = 0; index < groups.Count; index++)
        {
            if (index < Groups.Count && ReferenceEquals(Groups[index], groups[index])) continue;

            var currentIndex = Groups.IndexOf(groups[index]);
            if (currentIndex >= 0)
                Groups.Move(currentIndex, index);
            else
                Groups.Insert(index, groups[index]);
        }

        while (Groups.Count > groups.Count) Groups.RemoveAt(Groups.Count - 1);
    }

    private static void ReconcileItems(
        ObservableCollection<ClipItemViewModel> collection,
        IReadOnlyList<ClipItemViewModel> desired)
    {
        var desiredIds = desired.Select(item => item.Id).ToHashSet();
        for (var index = collection.Count - 1; index >= 0; index--)
            if (!desiredIds.Contains(collection[index].Id)) collection.RemoveAt(index);

        for (var index = 0; index < desired.Count; index++)
        {
            if (index < collection.Count && ReferenceEquals(collection[index], desired[index])) continue;

            var currentIndex = collection.IndexOf(desired[index]);
            if (currentIndex >= 0)
                collection.Move(currentIndex, index);
            else
                collection.Insert(index, desired[index]);
        }

        while (collection.Count > desired.Count) collection.RemoveAt(collection.Count - 1);
    }

    private static void AddGroup(
        ICollection<(string TitleKey, ClipItemViewModel[] Items)> groups,
        string titleKey,
        IEnumerable<ClipItemViewModel> items)
    {
        var rows = items.ToArray();
        if (rows.Length > 0) groups.Add((titleKey, rows));
    }

    // Destructive: the Clear button shows an in-window confirmation panel first.
    [RelayCommand]
    private void RequestClear() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    // One command for both the Select and Cancel buttons.
    [RelayCommand]
    private void ToggleSelection() => IsSelecting = !IsSelecting;

    [RelayCommand]
    private void CopySelected()
    {
        // Images have no text to join; they are left out of a multi-copy.
        var texts = Items.Where(item => item.IsSelected && !item.HasImage).Select(item => item.Text).ToArray();
        if (texts.Length == 0) return;

        _ = _clipboard.WriteText(string.Join("\r\n", texts));
        ShowCopyNotice();
        IsSelecting = false;
        SelectionCopied?.Invoke(this, EventArgs.Empty);
    }

    // Called by a row when its check changes so the footer count keeps up.
    internal void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionCountText));
        OnPropertyChanged(nameof(CanCopySelection));
        OnPropertyChanged(nameof(FooterText));
    }

    [RelayCommand]
    private async Task ConfirmClearAsync()
    {
        await _repository.ClearUnpinnedAsync(CancellationToken.None);

        // The clear only removes unpinned, uncategorized rows: a pending delete for anything else
        // must still land, or the row would silently come back without its undo.
        var survivors = _pendingDeletes
            .Where(entry => entry.Item.IsPinned || entry.Item.CategoryId is not null)
            .ToList();
        foreach (var pending in survivors)
        {
            _pendingDeletes.Remove(pending);
            pending.Cts.Cancel();
            await _repository.DeleteAsync(pending.Id, CancellationToken.None, Unlocked);
        }

        ClearPendingDeletes();                        // the rest were just removed by the bulk clear
        IsConfirmingClear = false;
    }

    partial void OnIsSelectingChanged(bool value)
    {
        if (!value)
            foreach (var item in Items) item.IsSelected = false;

        foreach (var item in Items) item.NotifySelectionModeChanged();
        OnPropertyChanged(nameof(ShowSelectButton));
        OnPropertyChanged(nameof(IsShowingCopyNotice));
        OnPropertyChanged(nameof(ShowFooterText));
        OnPropertyChanged(nameof(FooterText));
    }

    // Debounced: the previous query is cancelled as soon as a newer one arrives.
    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(CanSearch));
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _ = SearchDebouncedAsync(cts.Token);
    }

    private async Task SearchDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, ct);
            if (!ct.IsCancellationRequested) await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer query
        }
    }
}
