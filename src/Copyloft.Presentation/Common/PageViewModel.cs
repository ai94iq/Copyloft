using CommunityToolkit.Mvvm.Messaging;

namespace Copyloft.Presentation.Common;

// Base for every page ViewModel: one implementation of loading, empty, error and retry.
// Derived pages implement LoadCoreAsync and return false when there is nothing to show.
public abstract partial class PageViewModel : ObservableObject
{
    private readonly ILogger _log;
    private bool _loadedOnce;

    protected PageViewModel(ILogger log)
    {
        _log = log;
        State = LoadState.Idle;
    }

    [ObservableProperty]
    public partial LoadState State { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    // Call from the page's navigated-to hook. Loads the first time only; later visits reuse the data.
    public Task EnsureLoadedAsync() =>
        !_loadedOnce && ReloadCommand.CanExecute(null) ? ReloadCommand.ExecuteAsync(null) : Task.CompletedTask;

    protected abstract Task<bool> LoadCoreAsync(CancellationToken ct);

    protected void MarkQuietLoadCompleted(bool hasContent)
    {
        _loadedOnce = true;
        ErrorMessage = null;
        State = hasContent ? LoadState.Loaded : LoadState.Empty;
    }

    protected void MarkQuietLoadFailed(Exception ex)
    {
        _log.LogError(ex, "Loading {Page} failed", GetType().Name);
        if (_loadedOnce) return;

        ErrorMessage = Tr.Get("Error_LoadFailed");
        State = LoadState.Error;
    }

    // For background work outside a page load: log it and leave the UI as it is.
    protected void LogQuietFailure(Exception ex) =>
        _log.LogError(ex, "{Page} background work failed", GetType().Name);

    // Reload automatically when a repository reports a change in one of these areas. Hidden views
    // skip the refresh and load fresh data when they are shown again, so a closed flyout does no
    // background work (and no backdrop recomposition) on every clipboard change.
    protected void ReloadOnChange(params string[] areas) =>
        WeakReferenceMessenger.Default.Register<PageViewModel, DataChanged>(this, (vm, message) =>
        {
            vm.OnDataChanged(message);
            if (vm.IsViewVisible && areas.Contains(message.Area) &&
                vm._loadedOnce && vm.ReloadCommand.CanExecute(null))
                vm.ReloadCommand.Execute(null);
        });

    protected void ReloadQuietlyOnChange(params string[] areas) =>
        WeakReferenceMessenger.Default.Register<PageViewModel, DataChanged>(this, (vm, message) =>
        {
            vm.OnDataChanged(message);
            if (vm.IsViewVisible && areas.Contains(message.Area) && vm._loadedOnce)
                _ = vm.ReloadQuietlyAsync();
        });

    // Every change, visible or not, before any reload; for state that must not wait for a reload.
    protected virtual void OnDataChanged(DataChanged change)
    {
    }

    // The view keeps this in step with its visibility; hidden views refresh on show instead.
    public bool IsViewVisible { get; set; } = true;

    protected virtual Task ReloadQuietlyAsync() => ReloadCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task ReloadAsync(CancellationToken ct)
    {
        State = LoadState.Loading;
        ErrorMessage = null;
        try
        {
            State = await LoadCoreAsync(ct) ? LoadState.Loaded : LoadState.Empty;
            _loadedOnce = true;
        }
        catch (OperationCanceledException)
        {
            State = LoadState.Idle;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading {Page} failed", GetType().Name);
            ErrorMessage = Tr.Get("Error_LoadFailed");
            State = LoadState.Error;
        }
    }
}
