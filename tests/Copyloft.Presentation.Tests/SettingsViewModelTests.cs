using Copyloft.Presentation.Features.Settings;
using Copyloft.Presentation.Hosting;
using Copyloft.Presentation.Localization;
using Copyloft.Presentation.Services;
using Copyloft.Core.Abstractions;
using Copyloft.Core.Export;
using Copyloft.Core.Input;
using Copyloft.Core.Models;
using Copyloft.Core.Theming;
using NSubstitute;

namespace Copyloft.Presentation.Tests;

public sealed class SettingsViewModelTests
{
    private readonly FakeSettingsStore _store = new();
    private readonly FakeSecureSettings _secure = new();
    private readonly SettingsService _service;
    private readonly IThemeService _theme = Substitute.For<IThemeService>();
    private readonly IClipRepository _repository = Substitute.For<IClipRepository>();
    private readonly IClipExportService _export = Substitute.For<IClipExportService>();
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly CategoryLockService _locks;
    private readonly ILockPasswordService _passwords;
    private readonly IUpdateChecker _updates = Substitute.For<IUpdateChecker>();
    private readonly IHotkeyRegistration _hotkeyRegistration = Substitute.For<IHotkeyRegistration>();
    private readonly FakeStartupRegistration _startup = new();

    public SettingsViewModelTests()
    {
        _service = new SettingsService(_store, new AppSettings());
        _passwords = new LockPasswordService(_service, _secure, TimeProvider.System);
        _locks = new CategoryLockService(_service, Substitute.For<IDataChangeNotifier>(), _secure, TimeProvider.System);
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(true);
    }

    [Fact]
    public void Toggling_double_click_off_saves_the_setting()
    {
        var viewModel = Create();

        viewModel.DoubleClickOpensHistory = false;

        Assert.True(_store.Saved is { OpenHistoryOnDoubleClick: false });
    }

    [Fact]
    public void Changing_the_theme_saves_and_applies_it()
    {
        var viewModel = Create();

        viewModel.SelectedTheme = viewModel.ThemeOptions.Single(o => o.Value == AppTheme.Dark);

        Assert.True(_store.Saved is { Theme: AppTheme.Dark });
        _theme.Received(1).Apply(Arg.Is<AppSettings>(s => s.Theme == AppTheme.Dark));
    }

    [Fact]
    public void Changing_the_backdrop_saves_and_applies_it()
    {
        var viewModel = Create();

        viewModel.SelectedBackdrop = viewModel.BackdropOptions.Single(o => o.Value == BackdropKind.Acrylic);

        Assert.True(_store.Saved is { Backdrop: BackdropKind.Acrylic });
        _theme.Received(1).Apply(Arg.Is<AppSettings>(s => s.Backdrop == BackdropKind.Acrylic));
    }

    [Fact]
    public void Changing_the_tray_icon_style_saves_it()
    {
        var viewModel = Create();

        viewModel.SelectedTrayIcon = viewModel.TrayIconOptions.Single(o => o.Value == TrayIconKind.Outline);

        Assert.True(_store.Saved is { TrayIcon: TrayIconKind.Outline });
    }

    [Fact]
    public void Hiding_the_tray_icon_saves_it()
    {
        var viewModel = Create();

        viewModel.ShowTrayIcon = false;

        Assert.True(_store.Saved is { ShowTrayIcon: false });
    }

    [Fact]
    public void Existing_settings_are_preselected()
    {
        var viewModel = Create();

        Assert.Equal(AppTheme.System, viewModel.SelectedTheme.Value);
        Assert.Equal(BackdropKind.Mica, viewModel.SelectedBackdrop.Value);
    }

    // Avalonia has one Mica (probed: the window's DWM system backdrop type stays 0), so Mica Alt
    // looked identical; it is no longer offered, and a saved one shows as Mica.
    [Fact]
    public void Mica_Alt_is_not_offered_and_a_saved_one_shows_as_Mica()
    {
        _service.Update(_service.Current with { Backdrop = BackdropKind.MicaAlt });

        var viewModel = Create();

        Assert.DoesNotContain(viewModel.BackdropOptions, o => o.Value == BackdropKind.MicaAlt);
        Assert.Equal(BackdropKind.Mica, viewModel.SelectedBackdrop.Value);
    }

    [Fact]
    public void Changing_the_hotkey_saves_it()
    {
        var viewModel = Create();

        viewModel.Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);

        Assert.True(_store.Saved is
        {
            Hotkey: { Modifiers: HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey: 0x43 },
        });
    }

    [Fact]
    public void The_saved_hotkey_is_shown_when_the_page_loads()
    {
        _service.Update(_service.Current with
        {
            Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x41),
        });

        var viewModel = Create();

        Assert.Equal(Copyloft.Core.Input.HotkeyText.Display(new Copyloft.Core.Input.HotkeyGesture(Copyloft.Core.Input.HotkeyModifiers.Control | Copyloft.Core.Input.HotkeyModifiers.Shift, 0x41)), viewModel.HotkeyDisplay);
    }

    [Fact]
    public void A_taken_hotkey_warns_and_keeps_the_previous_shortcut_active()
    {
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(false);
        _hotkeyRegistration.RegisteredHotkey
            .Returns(new HotkeyGesture(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x56));
        var viewModel = Create();

        viewModel.Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);

        Assert.True(viewModel.HasHotkeyWarning);
        Assert.Equal(Tr.Get("Settings_HotkeyConflict"), viewModel.HotkeyConflictTitle);
        Assert.Equal(
            Tr.Format("Settings_HotkeyStillUsing", Copyloft.Core.Input.HotkeyText.Display(new Copyloft.Core.Input.HotkeyGesture(Copyloft.Core.Input.HotkeyModifiers.Win | Copyloft.Core.Input.HotkeyModifiers.Shift, 0x56))),
            viewModel.HotkeyConflictDetail);
    }

    [Fact]
    public void A_taken_hotkey_with_no_active_shortcut_explains_it()
    {
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(false);
        _hotkeyRegistration.RegisteredHotkey.Returns((HotkeyGesture?)null);
        var viewModel = Create();

        viewModel.Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);

        Assert.True(viewModel.HasHotkeyWarning);
        Assert.Equal(Tr.Get("Settings_HotkeyNoShortcut"), viewModel.HotkeyConflictDetail);
    }

    [Fact]
    public void A_working_hotkey_shows_no_warning()
    {
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(true);
        _hotkeyRegistration.RegisteredHotkey
            .Returns(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43));
        var viewModel = Create();

        viewModel.Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);

        Assert.False(viewModel.HasHotkeyWarning);
        Assert.Empty(viewModel.HotkeyAlternatives);
    }

    [Fact]
    public void The_warning_offers_alternatives_that_are_not_already_taken()
    {
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(false);
        _hotkeyRegistration.RegisteredHotkey
            .Returns(new HotkeyGesture(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x56));
        var viewModel = Create();
        viewModel.Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);

        Assert.NotEmpty(viewModel.HotkeyAlternatives);
        Assert.DoesNotContain(viewModel.HotkeyAlternatives, option => option.Value == viewModel.Hotkey);
        Assert.DoesNotContain(
            viewModel.HotkeyAlternatives,
            option => option.Value == _hotkeyRegistration.RegisteredHotkey);
    }

    [Fact]
    public void Choosing_an_alternative_sets_the_hotkey()
    {
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(false);
        _hotkeyRegistration.RegisteredHotkey.Returns((HotkeyGesture?)null);
        var viewModel = Create();
        viewModel.Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);
        var alternative = viewModel.HotkeyAlternatives[0].Value;

        viewModel.ApplyHotkeyAlternative(alternative);

        Assert.Equal(alternative, _store.Saved?.Hotkey);
    }

    [Fact]
    public void Applying_a_working_alternative_clears_the_warning()
    {
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(false);
        _hotkeyRegistration.RegisteredHotkey.Returns((HotkeyGesture?)null);
        var viewModel = Create();
        viewModel.Hotkey = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);
        Assert.True(viewModel.HasHotkeyWarning);
        var alternative = viewModel.HotkeyAlternatives[0].Value;
        _hotkeyRegistration.IsCurrentHotkeyRegistered.Returns(true);
        _hotkeyRegistration.RegisteredHotkey.Returns(alternative);

        viewModel.ApplyHotkeyAlternative(alternative);

        Assert.False(viewModel.HasHotkeyWarning);
    }

    [Fact]
    public void Changing_the_language_saves_it()
    {
        var viewModel = Create();

        viewModel.SelectedLanguage = viewModel.LanguageOptions.Single(o => o.Value == "ar-SA");

        Assert.True(_store.Saved is { Language: "ar-SA" });
    }

    [Fact]
    public void Changing_the_language_flags_a_restart()
    {
        var viewModel = Create();

        viewModel.SelectedLanguage = viewModel.LanguageOptions.Single(o => o.Value == "ar-SA");

        Assert.True(viewModel.HasLanguageChange);
        Assert.True(viewModel.NeedsRestart);
    }

    [Fact]
    public void Going_back_to_the_saved_language_needs_no_restart()
    {
        var viewModel = Create();
        viewModel.SelectedLanguage = viewModel.LanguageOptions.Single(o => o.Value == "ar-SA");

        viewModel.SelectedLanguage = viewModel.LanguageOptions.Single(o => o.Value == "en-US");

        Assert.False(viewModel.HasLanguageChange);
    }

    [Fact]
    public void The_accent_options_offer_windows_and_every_preset()
    {
        var viewModel = Create();

        Assert.Equal(AccentPresets.All.Count + 1, viewModel.AccentOptions.Count);
        Assert.Null(viewModel.AccentOptions[0].Value);
        Assert.Equal(AccentPresets.All.Select(p => p.Hex), viewModel.AccentOptions.Skip(1).Select(o => o.Value));
        Assert.True(viewModel.AccentOptions[0].IsSelected);
    }

    [Fact]
    public void Choosing_an_accent_saves_it_and_flags_a_restart()
    {
        var viewModel = Create();
        var purple = AccentPresets.All.Single(p => p.Key == "Purple").Hex;

        viewModel.SelectedAccent = viewModel.AccentOptions.Single(o => o.Value == purple);

        Assert.Equal(purple, _store.Saved?.Accent);
        Assert.True(viewModel.HasAccentChange);
        Assert.True(viewModel.NeedsRestart);
        Assert.True(viewModel.SelectedAccent.IsSelected);
        Assert.Single(viewModel.AccentOptions, o => o.IsSelected);
    }

    [Fact]
    public void Going_back_to_the_saved_accent_needs_no_restart()
    {
        var viewModel = Create();
        var purple = AccentPresets.All.Single(p => p.Key == "Purple").Hex;
        viewModel.SelectedAccent = viewModel.AccentOptions.Single(o => o.Value == purple);

        viewModel.SelectedAccent = viewModel.AccentOptions[0];

        Assert.False(viewModel.HasAccentChange);
        Assert.False(viewModel.NeedsRestart);
    }

    [Fact]
    public void A_saved_accent_is_preselected()
    {
        var green = AccentPresets.All.Single(p => p.Key == "Green").Hex;
        _service.Update(_service.Current with { Accent = green });

        var viewModel = Create();

        Assert.Equal(green, viewModel.SelectedAccent.Value);
        Assert.False(viewModel.HasAccentChange);
    }

    [Fact]
    public void Turning_on_run_at_startup_saves_and_applies_it()
    {
        var viewModel = Create();

        viewModel.RunAtStartup = true;

        Assert.True(_store.Saved is { RunAtStartup: true });
        Assert.Equal(new[] { true }, _startup.Applied);
    }

    [Fact]
    public void Turning_off_run_at_startup_applies_false()
    {
        var viewModel = Create();
        viewModel.RunAtStartup = true;

        viewModel.RunAtStartup = false;

        Assert.True(_store.Saved is { RunAtStartup: false });
        Assert.Equal(new[] { true, false }, _startup.Applied);
    }

    [Fact]
    public async Task Changing_max_items_saves_it_and_prunes_the_history()
    {
        var viewModel = Create();

        viewModel.MaxItems = 250;
        await Task.Delay(600, TestContext.Current.CancellationToken);

        Assert.True(_store.Saved is { MaxItems: 250 });
        await _repository.Received(1).PruneAsync(250, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rapid_max_item_changes_prune_once_with_the_final_value()
    {
        var viewModel = Create();

        viewModel.MaxItems = 50;
        viewModel.MaxItems = 100;
        viewModel.MaxItems = 250;
        await Task.Delay(600, TestContext.Current.CancellationToken);

        await _repository.Received(1).PruneAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).PruneAsync(250, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Loading_does_not_write_settings()
    {
        _ = Create();

        Assert.Null(_store.Saved);
        Assert.Empty(_startup.Applied);
    }

    [Fact]
    public void An_empty_filter_shows_every_settings_group()
    {
        var viewModel = Create();

        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 0));
        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 1));
        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 2));
        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 3));
        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 4));
        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 5));
        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 6));
        Assert.True(viewModel.IsExitVisible(viewModel.Filter));
        Assert.False(viewModel.HasNoMatches(viewModel.Filter));
    }

    [Fact]
    public void Filtering_keeps_only_the_matching_group()
    {
        var viewModel = Create();
        viewModel.Filter = "startup";

        Assert.False(viewModel.IsGroupVisible(viewModel.Filter, 0));
        Assert.False(viewModel.IsGroupVisible(viewModel.Filter, 1));
        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 2));
        Assert.False(viewModel.IsExitVisible(viewModel.Filter));
        Assert.False(viewModel.HasNoMatches(viewModel.Filter));
    }

    [Fact]
    public void Filtering_matches_the_description_text_too()
    {
        var viewModel = Create();
        viewModel.Filter = "starts hidden";

        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 2));
    }

    [Fact]
    public void A_filter_with_no_matches_hides_everything()
    {
        var viewModel = Create();
        viewModel.Filter = "zzz";

        Assert.True(viewModel.HasNoMatches(viewModel.Filter));
    }

    [Fact]
    public void Changing_the_retention_saves_it_and_prunes_older_clips()
    {
        var viewModel = Create();

        viewModel.SelectedRetention = viewModel.RetentionOptions.Single(o => o.Value == 7);

        Assert.True(_store.Saved is { RetentionDays: 7 });
        _repository.Received(1).PruneOlderThanAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Choosing_forever_never_prunes_by_age()
    {
        var viewModel = Create();
        viewModel.SelectedRetention = viewModel.RetentionOptions.Single(o => o.Value == 7);
        _repository.ClearReceivedCalls();

        viewModel.SelectedRetention = viewModel.RetentionOptions.Single(o => o.Value == 0);

        Assert.True(_store.Saved is { RetentionDays: 0 });
        _repository.DidNotReceive().PruneOlderThanAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_saved_retention_is_preselected()
    {
        _service.Update(_service.Current with { RetentionDays = 30 });

        var viewModel = Create();

        Assert.Equal(30, viewModel.SelectedRetention.Value);
    }

    [Fact]
    public void Changing_clear_on_sign_out_saves_it()
    {
        var viewModel = Create();

        viewModel.ClearOnSignOut = true;

        Assert.True(_store.Saved is { ClearOnSignOut: true });
    }

    [Fact]
    public async Task Exporting_uses_the_chosen_format_and_reports_the_count()
    {
        _export.ExportAsync("out.json", ClipExportFormat.Json, Arg.Any<CancellationToken>()).Returns(3);
        var viewModel = Create();

        await viewModel.ExportAsync("out.json");

        await _export.Received(1).ExportAsync("out.json", ClipExportFormat.Json, Arg.Any<CancellationToken>());
        Assert.True(viewModel.HasExportStatus);
        Assert.False(viewModel.IsExportError);
        Assert.True(viewModel.ShowExportStatus);
        Assert.False(viewModel.ShowExportError);
        Assert.Equal(
            Tr.Format("Settings_ExportDone", Tr.Plural("History_Count", 3)),
            viewModel.ExportStatus);
    }

    [Fact]
    public async Task Choosing_csv_exports_csv()
    {
        _export.ExportAsync("out.csv", ClipExportFormat.Csv, Arg.Any<CancellationToken>()).Returns(0);
        var viewModel = Create();
        viewModel.SelectedExportFormat = viewModel.ExportFormatOptions.Single(o => o.Value == ClipExportFormat.Csv);

        await viewModel.ExportAsync("out.csv");

        await _export.Received(1).ExportAsync("out.csv", ClipExportFormat.Csv, Arg.Any<CancellationToken>());
        Assert.Equal(Tr.Get("Settings_ExportEmpty"), viewModel.ExportStatus);
    }

    [Fact]
    public async Task A_failed_export_reports_it()
    {
        _export.ExportAsync(Arg.Any<string>(), Arg.Any<ClipExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new IOException("disk full")));
        var viewModel = Create();

        await viewModel.ExportAsync("out.json");

        Assert.Equal(Tr.Get("Settings_ExportFailed"), viewModel.ExportStatus);
        Assert.True(viewModel.IsExportError);
        Assert.True(viewModel.ShowExportError);
        Assert.False(viewModel.ShowExportStatus);
    }

    [Fact]
    public async Task Retrying_the_export_clears_the_error_mark()
    {
        _export.ExportAsync(Arg.Any<string>(), Arg.Any<ClipExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new IOException("disk full")));
        var viewModel = Create();
        await viewModel.ExportAsync("out.json");

        _export.ExportAsync(Arg.Any<string>(), Arg.Any<ClipExportFormat>(), Arg.Any<CancellationToken>()).Returns(2);
        await viewModel.ExportAsync("out.json");

        Assert.False(viewModel.IsExportError);
    }

    [Fact]
    public async Task Exporting_disables_export_until_it_finishes()
    {
        var completion = new TaskCompletionSource<int>();
        _export.ExportAsync(Arg.Any<string>(), Arg.Any<ClipExportFormat>(), Arg.Any<CancellationToken>())
            .Returns(completion.Task);
        var viewModel = Create();

        var export = viewModel.ExportAsync("out.json");
        Assert.False(viewModel.CanExport);

        completion.SetResult(1);
        await export;

        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public void The_suggested_export_file_name_holds_the_app_name_and_a_timestamp()
    {
        var viewModel = Create();

        Assert.Matches(
            @"^Copyloft-Export-\d{4}-\d{2}-\d{2}-\d{4}$",
            viewModel.SuggestedExportFileName);
    }

    [Fact]
    public async Task Setting_the_password_stores_a_hash_that_verifies()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>()).Returns(new List<Category>());
        var viewModel = Create();

        await viewModel.SetLockPasswordAsync("hunter2");

        Assert.True(viewModel.HasLockPassword);
        Assert.True(_secure.Values.ContainsKey("LockPasswordHash"));
        Assert.Null(_service.Current.LockPasswordHash);
        Assert.True(viewModel.VerifyLockPassword("hunter2"));
        Assert.False(viewModel.VerifyLockPassword("wrong"));
    }

    [Fact]
    public async Task Removing_the_password_clears_it_and_every_lock()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>()).Returns(new List<Category>());
        var viewModel = Create();
        await viewModel.SetLockPasswordAsync("hunter2");

        await viewModel.RemoveLockPasswordAsync();

        Assert.False(viewModel.HasLockPassword);
        Assert.False(_secure.Values.ContainsKey("LockPasswordHash"));
        await _categories.Received(1).ClearLocksAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_password_button_says_set_then_change()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>()).Returns(new List<Category>());
        var viewModel = Create();
        var before = viewModel.PasswordButtonText;

        await viewModel.SetLockPasswordAsync("hunter2");

        Assert.NotEqual(before, viewModel.PasswordButtonText);
    }

    [Fact]
    public void Filtering_by_password_keeps_the_categories_group()
    {
        var viewModel = Create();
        viewModel.Filter = "password";

        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 5));
    }

    [Fact]
    public void Filtering_by_export_keeps_the_export_group()
    {
        var viewModel = Create();
        viewModel.Filter = "export";

        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 4));
    }

    [Fact]
    public async Task Category_rows_report_the_lock_state()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Work"), new(8, "Secret", true) });
        var viewModel = Create();

        await viewModel.RefreshCategoriesAsync();

        Assert.Equal(2, viewModel.CategoryRows.Count);
        Assert.True(viewModel.CategoryRows[0].ShowLock);
        Assert.True(viewModel.CategoryRows[1].ShowUnlock);
        Assert.False(viewModel.CategoryRows[1].ShowLockNow);
    }

    [Fact]
    public async Task Unlocking_a_category_swaps_the_lock_buttons()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(8, "Secret", true) });
        var viewModel = Create();
        await viewModel.RefreshCategoriesAsync();

        await viewModel.UnlockCategoryAsync(8);

        Assert.True(viewModel.CategoryRows[0].ShowLockNow);
        Assert.Contains(8, _locks.UnlockedCategoryIds);
    }

    [Fact]
    public async Task Locking_a_category_marks_it_and_hides_it_again()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(8, "Secret") });
        var viewModel = Create();
        _locks.Unlock(8);

        await viewModel.LockCategoryAsync(8);

        await _categories.Received(1).SetLockedAsync(8, true, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(8, _locks.UnlockedCategoryIds);
    }

    [Fact]
    public void Filtering_by_categories_keeps_the_categories_group()
    {
        var viewModel = Create();
        viewModel.Filter = "categories";

        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 5));
    }

    [Fact]
    public void Toggling_lock_on_minimize_saves_it()
    {
        var viewModel = Create();

        viewModel.LockOnMinimize = true;

        Assert.True(_store.Saved is { LockOnMinimize: true });
    }

    [Fact]
    public void Lock_timing_has_safe_defaults()
    {
        var viewModel = Create();

        Assert.True(viewModel.LockOnExit);
        Assert.True(viewModel.LockOnShutdown);
        Assert.False(viewModel.LockOnMinimize);
    }

    [Fact]
    public void The_lock_timing_summary_lists_the_enabled_events()
    {
        var viewModel = Create();
        Assert.Equal(
            $"{Tr.Get("LockWhen_Exit")}, {Tr.Get("LockWhen_Shutdown")}",
            viewModel.LockWhenText);

        viewModel.LockOnExit = false;
        viewModel.LockOnShutdown = false;

        Assert.Equal(Tr.Get("LockWhen_Never"), viewModel.LockWhenText);

        viewModel.LockOnMinimize = true;

        Assert.Equal(Tr.Get("LockWhen_Minimize"), viewModel.LockWhenText);
    }

    [Fact]
    public void Full_black_is_offered_and_saved()
    {
        var viewModel = Create();

        viewModel.SelectedTheme = viewModel.ThemeOptions.Single(o => o.Value == AppTheme.Black);

        Assert.True(_store.Saved is { Theme: AppTheme.Black });
        _theme.Received(1).Apply(Arg.Is<AppSettings>(s => s.Theme == AppTheme.Black));
    }

    [Fact]
    public void A_saved_full_white_theme_loads_as_light()
    {
        _service.Update(_service.Current with { Theme = AppTheme.White });

        var viewModel = Create();

        Assert.Equal(AppTheme.Light, viewModel.SelectedTheme.Value);
    }

    [Fact]
    public async Task Renaming_a_category_refreshes_the_list()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Category> { new(7, "Projects") });
        _categories.RenameAsync(7, "Projects", Arg.Any<CancellationToken>()).Returns(true);
        var viewModel = Create();

        var renamed = await viewModel.RenameCategoryAsync(7, " Projects ");

        Assert.True(renamed);
        await _categories.Received(1).RenameAsync(7, "Projects", Arg.Any<CancellationToken>());
        Assert.Equal("Projects", Assert.Single(viewModel.CategoryRows).Name);
    }

    [Fact]
    public async Task Renaming_to_a_taken_name_changes_nothing()
    {
        _categories.RenameAsync(7, "Work", Arg.Any<CancellationToken>()).Returns(false);
        var viewModel = Create();

        var renamed = await viewModel.RenameCategoryAsync(7, "Work");

        Assert.False(renamed);
        await _categories.DidNotReceive().GetCategoriesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_category_forgets_its_unlock()
    {
        _categories.GetCategoriesAsync(Arg.Any<CancellationToken>()).Returns(new List<Category>());
        var viewModel = Create();
        _locks.Unlock(8);

        await viewModel.DeleteCategoryAsync(8);

        await _categories.Received(1).DeleteAsync(8, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(8, _locks.UnlockedCategoryIds);
    }

    [Fact]
    public async Task Checking_for_updates_reports_a_newer_release()
    {
        _updates.NewerVersionAsync(Arg.Any<CancellationToken>()).Returns("0.3.0");
        var viewModel = Create();

        await viewModel.CheckForUpdatesAsync();

        Assert.True(viewModel.UpdateAvailable);
        Assert.Equal(Tr.Format("Settings_UpdateAvailable", "0.3.0"), viewModel.UpdateStatus);
        Assert.False(viewModel.IsUpdateError);
        Assert.True(viewModel.ShowUpdateStatus);
        Assert.False(viewModel.ShowUpdateError);
    }

    [Fact]
    public async Task An_up_to_date_check_reports_the_current_version()
    {
        _updates.NewerVersionAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        var viewModel = Create();

        await viewModel.CheckForUpdatesAsync();

        Assert.False(viewModel.UpdateAvailable);
        Assert.Equal(Tr.Format("Settings_UpdateCurrent", UpdateChecker.CurrentVersion), viewModel.UpdateStatus);
    }

    [Fact]
    public async Task A_failed_check_reports_it()
    {
        _updates.NewerVersionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new IOException("offline")));
        var viewModel = Create();

        await viewModel.CheckForUpdatesAsync();

        Assert.Equal(Tr.Get("Settings_UpdateFailed"), viewModel.UpdateStatus);
        Assert.True(viewModel.IsUpdateError);
    }

    [Fact]
    public async Task Retrying_the_update_check_clears_the_error_mark()
    {
        _updates.NewerVersionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new IOException("offline")));
        var viewModel = Create();
        await viewModel.CheckForUpdatesAsync();

        _updates.NewerVersionAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        await viewModel.CheckForUpdatesAsync();

        Assert.False(viewModel.IsUpdateError);
    }

    [Fact]
    public async Task The_automatic_check_runs_once()
    {
        _updates.NewerVersionAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        var viewModel = Create();

        await viewModel.CheckForUpdatesOnceAsync();
        await viewModel.CheckForUpdatesOnceAsync();

        await _updates.Received(1).NewerVersionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_automatic_check_tries_again_next_time()
    {
        _updates.NewerVersionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new IOException("offline")));
        var viewModel = Create();

        await viewModel.CheckForUpdatesOnceAsync();
        await viewModel.CheckForUpdatesOnceAsync();

        await _updates.Received(2).NewerVersionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Filtering_by_updates_keeps_the_updates_group()
    {
        var viewModel = Create();
        viewModel.Filter = "updates";

        Assert.True(viewModel.IsGroupVisible(viewModel.Filter, 6));
    }

    private SettingsViewModel Create() =>
        new(_service, _theme, _repository, _startup, _export, _categories, _locks, _passwords, _updates,
            new Lazy<IHotkeyRegistration>(() => _hotkeyRegistration),
            NullLogger<SettingsViewModel>.Instance);
}
