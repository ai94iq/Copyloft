using System.Collections.ObjectModel;
using Copyloft.Presentation.Services;
using Copyloft.Core.Abstractions;
using Copyloft.Core.Export;
using Copyloft.Core.Input;
using Copyloft.Core.Theming;
using CommunityToolkit.Mvvm.Messaging;

namespace Copyloft.Presentation.Features.Settings;

// Settings that apply immediately: each change is saved and takes effect without a restart.
public sealed partial class SettingsViewModel : ObservableObject
{
    private const int MinHistoryItems = 5;
    private const int MaxHistoryItems = 1000;
    private const int PruneDelayMs = 300;

    private readonly SettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IClipRepository _repository;
    private readonly IStartupRegistration _startup;
    private readonly IClipExportService _export;
    private readonly ICategoryRepository _categories;
    private readonly ICategoryLockService _locks;
    private readonly ILockPasswordService _passwords;
    private readonly IUpdateChecker _updates;
    private readonly Lazy<IHotkeyRegistration> _hotkeyRegistration;
    private readonly ILogger<SettingsViewModel> _log;
    private readonly string _initialLanguage;
    private readonly string? _initialAccent;
    private bool _checkedForUpdates;
    private CancellationTokenSource? _pruneCts;
    private bool _loading = true;

    public SettingsViewModel(
        SettingsService settings,
        IThemeService theme,
        IClipRepository repository,
        IStartupRegistration startup,
        IClipExportService export,
        ICategoryRepository categories,
        ICategoryLockService locks,
        ILockPasswordService passwords,
        IUpdateChecker updates,
        Lazy<IHotkeyRegistration> hotkeyRegistration,
        ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _theme = theme;
        _repository = repository;
        _startup = startup;
        _export = export;
        _categories = categories;
        _locks = locks;
        _passwords = passwords;
        _updates = updates;
        _hotkeyRegistration = hotkeyRegistration;
        _log = log;
        _initialLanguage = settings.Current.Language;
        _initialAccent = settings.Current.Accent;

        SelectedTheme = ThemeOptions.FirstOrDefault(o => o.Value == settings.Current.Theme)
            ?? ThemeOptions.First(o => o.Value == AppTheme.Light);
        // Mica Alt is no longer offered (Avalonia has one Mica); a saved one shows as Mica.
        var backdrop = settings.Current.Backdrop == BackdropKind.MicaAlt ? BackdropKind.Mica : settings.Current.Backdrop;
        SelectedBackdrop = BackdropOptions.First(o => o.Value == backdrop);
        SelectedTrayIcon = TrayIconOptions.First(o => o.Value == settings.Current.TrayIcon);
        SelectedLanguage = LanguageOptions.FirstOrDefault(o => o.Value == settings.Current.Language) ?? LanguageOptions[0];
        AccentOptions = BuildAccentOptions();
        SelectedAccent = AccentOptions.FirstOrDefault(o => o.Value == settings.Current.Accent) ?? AccentOptions[0];
        SelectedRetention = RetentionOptions.FirstOrDefault(o => o.Value == settings.Current.RetentionDays)
            ?? RetentionOptions[^1];
        ShowTrayIcon = settings.Current.ShowTrayIcon;
        DoubleClickOpensHistory = settings.Current.OpenHistoryOnDoubleClick;
        Hotkey = settings.Current.Hotkey;
        RunAtStartup = settings.Current.RunAtStartup;
        MaxItems = settings.Current.MaxItems;
        ClearOnSignOut = settings.Current.ClearOnSignOut;
        LockOnExit = settings.Current.LockOnExit;
        LockOnMinimize = settings.Current.LockOnMinimize;
        LockOnShutdown = settings.Current.LockOnShutdown;
        SelectedExportFormat = ExportFormatOptions[0];
        WeakReferenceMessenger.Default.Register<SettingsViewModel, DataChanged>(this, (vm, message) =>
        {
            if (message.Area == "category") _ = vm.RefreshCategoriesAsync();
        });

        _loading = false;
        RefreshHotkeyState();
    }

    public IReadOnlyList<Option<AppTheme>> ThemeOptions { get; } =
    [
        new(AppTheme.System, Tr.Get("Theme_System")),
        new(AppTheme.Light, Tr.Get("Theme_Light")),
        new(AppTheme.Dark, Tr.Get("Theme_Dark")),
        new(AppTheme.Black, Tr.Get("Theme_Black")),
    ];

    public IReadOnlyList<Option<BackdropKind>> BackdropOptions { get; } =
    [
        new(BackdropKind.Mica, Tr.Get("Backdrop_Mica")),
        new(BackdropKind.Acrylic, Tr.Get("Backdrop_Acrylic")),
        new(BackdropKind.None, Tr.Get("Backdrop_None")),
    ];

    // "Windows accent" first, then the presets; labels come from resx keys "Accent_{Key}".
    public IReadOnlyList<AccentOption> AccentOptions { get; }

    private static IReadOnlyList<AccentOption> BuildAccentOptions()
    {
        var options = new List<AccentOption> { new(null, Tr.Get("Accent_Windows")) };
        options.AddRange(AccentPresets.All.Select(preset => new AccentOption(preset.Hex, Tr.Get($"Accent_{preset.Key}"))));
        return options;
    }

    public IReadOnlyList<Option<TrayIconKind>> TrayIconOptions { get; } =
    [
        new(TrayIconKind.Filled, Tr.Get("TrayIcon_Filled")),
        new(TrayIconKind.Outline, Tr.Get("TrayIcon_Outline")),
    ];

    // Language names are endonyms: they stay the same in every language.
    public IReadOnlyList<Option<string>> LanguageOptions { get; } =
    [
        new("en-US", "English"),
        new("ar-SA", "العربية"),
    ];

    public IReadOnlyList<Option<int>> RetentionOptions { get; } =
    [
        new(1, Tr.Get("Retention_Day")),
        new(7, Tr.Get("Retention_Week")),
        new(30, Tr.Get("Retention_Month")),
        new(0, Tr.Get("Retention_Forever")),
    ];

    [ObservableProperty]
    public partial Option<AppTheme> SelectedTheme { get; set; }

    [ObservableProperty]
    public partial Option<BackdropKind> SelectedBackdrop { get; set; }

    [ObservableProperty]
    public partial AccentOption SelectedAccent { get; set; }

    [ObservableProperty]
    public partial Option<TrayIconKind> SelectedTrayIcon { get; set; }

    [ObservableProperty]
    public partial Option<string> SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; }

    [ObservableProperty]
    public partial bool DoubleClickOpensHistory { get; set; }

    [ObservableProperty]
    public partial HotkeyGesture Hotkey { get; set; }

    [ObservableProperty]
    public partial bool RunAtStartup { get; set; }

    [ObservableProperty]
    public partial double MaxItems { get; set; }

    [ObservableProperty]
    public partial Option<int> SelectedRetention { get; set; }

    [ObservableProperty]
    public partial bool ClearOnSignOut { get; set; }

    [ObservableProperty]
    public partial bool LockOnExit { get; set; }

    [ObservableProperty]
    public partial bool LockOnMinimize { get; set; }

    [ObservableProperty]
    public partial bool LockOnShutdown { get; set; }

    // Summary on the "Lock categories when" dropdown: "on exit, on shutdown" or "never".
    public string LockWhenText
    {
        get
        {
            var parts = new List<string>(3);
            if (LockOnExit) parts.Add(Tr.Get("LockWhen_Exit"));
            if (LockOnMinimize) parts.Add(Tr.Get("LockWhen_Minimize"));
            if (LockOnShutdown) parts.Add(Tr.Get("LockWhen_Shutdown"));
            return parts.Count == 0 ? Tr.Get("LockWhen_Never") : string.Join(", ", parts);
        }
    }

    public IReadOnlyList<Option<ClipExportFormat>> ExportFormatOptions { get; } =
    [
        new(ClipExportFormat.Json, Tr.Get("Export_Json")),
        new(ClipExportFormat.Csv, Tr.Get("Export_Csv")),
    ];

    [ObservableProperty]
    public partial Option<ClipExportFormat> SelectedExportFormat { get; set; }

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    // Feedback under the export row: how many clips were written, or why it failed.
    [ObservableProperty]
    public partial string? ExportStatus { get; set; }

    // True while ExportStatus holds a failure, so the page shows it in red.
    [ObservableProperty]
    public partial bool IsExportError { get; set; }

    // The status line switches between the normal and the critical color; each state gets its own
    // TextBlock so the color follows the window's theme.
    public bool ShowExportStatus => HasExportStatus && !IsExportError;

    public bool ShowExportError => HasExportStatus && IsExportError;

    public bool CanExport => !IsExporting;

    public bool HasExportStatus => !string.IsNullOrEmpty(ExportStatus);

    // The shared password: asked before exporting and before opening a locked category.
    public bool HasLockPassword => _passwords.IsSet;

    public string PasswordButtonText =>
        HasLockPassword ? Tr.Get("Settings_ChangePassword") : Tr.Get("Settings_SetPassword");

    public bool VerifyLockPassword(string password) => _passwords.Verify(password);

    public TimeSpan LockPasswordRetryAfter => _passwords.RetryAfter;

    public async Task SetLockPasswordAsync(string password)
    {
        _passwords.Set(password);
        NotifyLockPasswordChanged();
        await RefreshCategoriesAsync();
    }

    public async Task RemoveLockPasswordAsync()
    {
        _passwords.Remove();
        _locks.Reset();
        await _categories.ClearLocksAsync(CancellationToken.None);
        NotifyLockPasswordChanged();
        await RefreshCategoriesAsync();
    }

    private void NotifyLockPasswordChanged()
    {
        OnPropertyChanged(nameof(HasLockPassword));
        OnPropertyChanged(nameof(PasswordButtonText));
    }

    // Update check: once per run from the page, or on demand from the button.
    [ObservableProperty]
    public partial string? UpdateStatus { get; set; }

    // True while UpdateStatus holds a failure, so the page shows it in red.
    [ObservableProperty]
    public partial bool IsUpdateError { get; set; }

    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingUpdates { get; set; }

    public bool CanCheckUpdates => !IsCheckingUpdates;

    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);

    // Same split as the export status: the color must follow the window's theme.
    public bool ShowUpdateStatus => HasUpdateStatus && !IsUpdateError;

    public bool ShowUpdateError => HasUpdateStatus && IsUpdateError;

    public async Task CheckForUpdatesOnceAsync()
    {
        if (_checkedForUpdates) return;

        // A failed check is retried on the next visit; a successful one is not repeated.
        _checkedForUpdates = await CheckForUpdatesAsync();
    }

    public async Task<bool> CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates) return false;

        IsCheckingUpdates = true;
        try
        {
            var newer = await _updates.NewerVersionAsync(CancellationToken.None);
            UpdateAvailable = newer is not null;
            UpdateStatus = newer is null
                ? Tr.Format("Settings_UpdateCurrent", UpdateChecker.CurrentVersion)
                : Tr.Format("Settings_UpdateAvailable", newer);
            IsUpdateError = false;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Checking for updates failed");
            UpdateAvailable = false;
            UpdateStatus = Tr.Get("Settings_UpdateFailed");
            IsUpdateError = true;
            return false;
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    partial void OnIsCheckingUpdatesChanged(bool value) => OnPropertyChanged(nameof(CanCheckUpdates));

    partial void OnUpdateStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HasUpdateStatus));
        OnPropertyChanged(nameof(ShowUpdateStatus));
        OnPropertyChanged(nameof(ShowUpdateError));
    }

    partial void OnIsUpdateErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUpdateStatus));
        OnPropertyChanged(nameof(ShowUpdateError));
    }

    // Category locks: each category can hide its clips behind its own password.
    public ObservableCollection<CategoryRow> CategoryRows { get; } = [];

    public bool ShowCategoriesEmpty => CategoryRows.Count == 0;

    public async Task RefreshCategoriesAsync()
    {
        var categories = await _categories.GetCategoriesAsync(CancellationToken.None);

        CategoryRows.Clear();
        foreach (var category in categories)
        {
            CategoryRows.Add(new CategoryRow(
                category.Id,
                category.Name,
                category.IsLocked,
                _locks.UnlockedCategoryIds.Contains(category.Id)));
        }

        OnPropertyChanged(nameof(ShowCategoriesEmpty));
    }

    public async Task LockCategoryAsync(long id)
    {
        await _categories.SetLockedAsync(id, true, CancellationToken.None);
        _locks.Lock(id);
        await RefreshCategoriesAsync();
    }

    public async Task UnlockCategoryAsync(long id)
    {
        _locks.Unlock(id);
        await RefreshCategoriesAsync();
    }

    public async Task<bool> RenameCategoryAsync(long id, string name)
    {
        var renamed = await _categories.RenameAsync(id, name.Trim(), CancellationToken.None);
        if (renamed) await RefreshCategoriesAsync();
        return renamed;
    }

    public async Task DeleteCategoryAsync(long id)
    {
        await _categories.DeleteAsync(id, CancellationToken.None);
        _locks.Lock(id);
        await RefreshCategoriesAsync();
    }

    // Default name for the save dialog: Copyloft-Export-2026-10-01-1432.
    public string SuggestedExportFileName =>
        Tr.Format("Settings_ExportFileName",
            Tr.Get("App_Name"),
            DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture));

    // Settings search from the title bar; an empty filter shows everything.
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    // Resx keys per settings group, in the page's order, so a search can hide whole groups.
    private static readonly string[][] Groups =
    [
        ["Settings_Theme", "Settings_Accent", "Settings_AccentHint", "Settings_Backdrop", "Theme_Black", "Settings_Language", "Settings_LanguageHint"],
        ["Settings_TrayIcon", "Settings_ShowTrayIcon", "Settings_DoubleClickTray"],
        [
            "Settings_RunAtStartup", "Settings_RunAtStartupHint",
            "Settings_Hotkey", "Settings_HotkeyHint",
        ],
        [
            "Settings_History", "Settings_MaxItems", "Settings_MaxItemsHint",
            "Settings_Retention", "Settings_RetentionHint",
            "Retention_Day", "Retention_Week", "Retention_Month", "Retention_Forever",
            "Settings_ClearOnSignOut", "Settings_ClearOnSignOutHint",
        ],
        ["Common_Export", "Settings_Export", "Settings_ExportHint"],
        [
            "Settings_Categories", "Settings_CategoriesEmpty",
            "Settings_LockPassword", "Settings_LockPasswordHint",
            "Settings_LockWhen", "Settings_LockOnExit", "Settings_LockOnMinimize", "Settings_LockOnShutdown",
            "Settings_LockCategory", "Settings_LockNow", "Settings_UnlockCategory", "Settings_RenameCategory",
        ],
        [
            "Settings_Updates", "Settings_CheckUpdates", "Settings_CheckUpdatesHint",
            "Settings_CheckUpdatesButton", "Settings_Download",
        ],
    ];

    private static readonly string[] ExitKeys = ["Settings_ExitApp", "Settings_ExitHint"];

    public bool ShowAppearanceGroup => IsGroupVisible(Filter, 0);

    public bool ShowTrayGroup => IsGroupVisible(Filter, 1);

    public bool ShowBehaviorGroup => IsGroupVisible(Filter, 2);

    public bool ShowHistoryGroup => IsGroupVisible(Filter, 3);

    public bool ShowExportGroup => IsGroupVisible(Filter, 4);

    public bool ShowCategoriesGroup => IsGroupVisible(Filter, 5);

    public bool ShowUpdatesGroup => IsGroupVisible(Filter, 6);

    public bool ShowExitGroup => IsExitVisible(Filter);

    public bool ShowNoMatches => HasNoMatches(Filter);

    public bool IsGroupVisible(string filter, int index) => MatchesAny(filter, Groups[index]);

    public bool IsExitVisible(string filter) => MatchesAny(filter, ExitKeys);

    public bool HasNoMatches(string filter) =>
        !Groups.Any(group => MatchesAny(filter, group)) && !MatchesAny(filter, ExitKeys);

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(ShowAppearanceGroup));
        OnPropertyChanged(nameof(ShowTrayGroup));
        OnPropertyChanged(nameof(ShowBehaviorGroup));
        OnPropertyChanged(nameof(ShowHistoryGroup));
        OnPropertyChanged(nameof(ShowExportGroup));
        OnPropertyChanged(nameof(ShowCategoriesGroup));
        OnPropertyChanged(nameof(ShowUpdatesGroup));
        OnPropertyChanged(nameof(ShowExitGroup));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    private static bool MatchesAny(string filter, string[] keys) =>
        string.IsNullOrWhiteSpace(filter) ||
        keys.Any(key => Tr.Get(key).Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));

    public string HotkeyDisplay => HotkeyText.Display(Hotkey);

    // Hotkey conflict warning: shown when the chosen shortcut could not be registered.
    [ObservableProperty]
    public partial bool HasHotkeyWarning { get; set; }

    [ObservableProperty]
    public partial string? HotkeyConflictTitle { get; set; }

    [ObservableProperty]
    public partial string? HotkeyConflictDetail { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<HotkeyGesture>> HotkeyAlternatives { get; set; } = [];

    partial void OnSelectedThemeChanged(Option<AppTheme> value) =>
        ApplyTheme(_settings.Current with { Theme = value.Value });

    partial void OnSelectedBackdropChanged(Option<BackdropKind> value) =>
        ApplyTheme(_settings.Current with { Backdrop = value.Value });

    partial void OnSelectedTrayIconChanged(Option<TrayIconKind> value) =>
        Save(_settings.Current with { TrayIcon = value.Value });

    // The language only applies on a fresh start; the page offers a restart when it differs.
    public bool HasLanguageChange => SelectedLanguage is { } selected && selected.Value != _initialLanguage;

    // The accent applies at startup too: WinUI reads the accent resources when controls load.
    public bool HasAccentChange => SelectedAccent.Value != _initialAccent;

    public bool NeedsRestart => HasLanguageChange || HasAccentChange;

    // AppSettings.Language takes effect on the next start; Culture.Configure reads it once.
    partial void OnSelectedLanguageChanged(Option<string> value)
    {
        Save(_settings.Current with { Language = value.Value });
        OnPropertyChanged(nameof(HasLanguageChange));
        OnPropertyChanged(nameof(NeedsRestart));
    }

    partial void OnSelectedAccentChanged(AccentOption value)
    {
        foreach (var option in AccentOptions) option.IsSelected = option == value;

        Save(_settings.Current with { Accent = value.Value });
        OnPropertyChanged(nameof(HasAccentChange));
        OnPropertyChanged(nameof(NeedsRestart));
    }

    partial void OnShowTrayIconChanged(bool value) =>
        Save(_settings.Current with { ShowTrayIcon = value });

    partial void OnDoubleClickOpensHistoryChanged(bool value) =>
        Save(_settings.Current with { OpenHistoryOnDoubleClick = value });

    partial void OnHotkeyChanged(HotkeyGesture value)
    {
        OnPropertyChanged(nameof(HotkeyDisplay));
        if (_loading) return;

        Save(_settings.Current with { Hotkey = value });
        RefreshHotkeyState();
    }

    // The view's alternative buttons call this; it goes through the same save/register path.
    public void ApplyHotkeyAlternative(HotkeyGesture gesture) => Hotkey = gesture;

    // Reads what actually registered; a failed change keeps the previous shortcut working.
    private void RefreshHotkeyState()
    {
        var registered = _hotkeyRegistration.Value.RegisteredHotkey;
        var conflict = !_hotkeyRegistration.Value.IsCurrentHotkeyRegistered;
        HasHotkeyWarning = conflict;
        HotkeyConflictTitle = conflict ? Tr.Get("Settings_HotkeyConflict") : null;
        HotkeyConflictDetail = conflict
            ? registered is null
                ? Tr.Get("Settings_HotkeyNoShortcut")
                : Tr.Format("Settings_HotkeyStillUsing", HotkeyText.Display(registered))
            : null;
        HotkeyAlternatives = conflict ? BuildHotkeyAlternatives(registered) : [];
    }

    private IReadOnlyList<Option<HotkeyGesture>> BuildHotkeyAlternatives(HotkeyGesture? registered)
    {
        HotkeyGesture[] candidates =
        [
            new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56),   // Ctrl+Shift+V
            new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56),     // Ctrl+Alt+V
            new(HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x56),         // Win+Alt+V
        ];
        return candidates
            .Where(candidate => candidate != Hotkey && candidate != registered)
            .Select(candidate => new Option<HotkeyGesture>(candidate, HotkeyText.Display(candidate)))
            .ToList();
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        if (_loading) return;

        Save(_settings.Current with { RunAtStartup = value });
        _startup.Apply(value);
    }

    partial void OnMaxItemsChanged(double value)
    {
        if (_loading) return;

        var keep = (int)Math.Clamp(value, MinHistoryItems, MaxHistoryItems);
        Save(_settings.Current with { MaxItems = keep });
        PruneDebounced(keep);
    }

    // The number box reports every keystroke or spin click; trim once the value settles instead
    // of deleting rows on each intermediate value.
    private void PruneDebounced(int keep)
    {
        _pruneCts?.Cancel();
        var cts = _pruneCts = new CancellationTokenSource();
        _ = PruneAfterDelayAsync(keep, cts.Token);
    }

    private async Task PruneAfterDelayAsync(int keep, CancellationToken ct)
    {
        try
        {
            await Task.Delay(PruneDelayMs, ct);
            await _repository.PruneAsync(keep, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer value
        }
    }

    partial void OnSelectedRetentionChanged(Option<int> value)
    {
        if (_loading) return;

        Save(_settings.Current with { RetentionDays = value.Value });
        if (value.Value > 0)
            _ = _repository.PruneOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-value.Value), CancellationToken.None);
    }

    partial void OnClearOnSignOutChanged(bool value)
    {
        if (_loading) return;

        Save(_settings.Current with { ClearOnSignOut = value });
    }

    partial void OnLockOnExitChanged(bool value)
    {
        Save(_settings.Current with { LockOnExit = value });
        OnPropertyChanged(nameof(LockWhenText));
    }

    partial void OnLockOnMinimizeChanged(bool value)
    {
        Save(_settings.Current with { LockOnMinimize = value });
        OnPropertyChanged(nameof(LockWhenText));
    }

    partial void OnLockOnShutdownChanged(bool value)
    {
        Save(_settings.Current with { LockOnShutdown = value });
        OnPropertyChanged(nameof(LockWhenText));
    }

    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    partial void OnExportStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HasExportStatus));
        OnPropertyChanged(nameof(ShowExportStatus));
        OnPropertyChanged(nameof(ShowExportError));
    }

    partial void OnIsExportErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExportStatus));
        OnPropertyChanged(nameof(ShowExportError));
    }

    // Runs after the page's save picker returned a file path.
    public async Task ExportAsync(string path)
    {
        if (IsExporting) return;

        IsExporting = true;
        ExportStatus = null;
        IsExportError = false;
        try
        {
            var count = await _export.ExportAsync(path, SelectedExportFormat.Value, CancellationToken.None);
            ExportStatus = count == 0
                ? Tr.Get("Settings_ExportEmpty")
                : Tr.Format("Settings_ExportDone", Tr.Plural("History_Count", count));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Exporting clips failed");
            ExportStatus = Tr.Get("Settings_ExportFailed");
            IsExportError = true;
        }
        finally
        {
            IsExporting = false;
        }
    }

    // Theme and backdrop show on screen right away, so they are re-applied as well as saved.
    private void ApplyTheme(AppSettings next)
    {
        if (_loading) return;

        _settings.Update(next);
        _theme.Apply(next);
    }

    private void Save(AppSettings next)
    {
        if (_loading) return;

        _settings.Update(next);
    }
}
