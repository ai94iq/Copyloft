using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Copyloft.Core.Export;
using Copyloft.Desktop.Controls;
using Copyloft.Core.Input;
using Copyloft.Desktop.Platform;
using Copyloft.Desktop.Shell;

namespace Copyloft.Desktop.Features;

// The Settings section of the main window. Every change saves and applies at once; the view
// model owns that, the page only adds what needs the UI (pickers, the hotkey recorder).
public sealed partial class SettingsPage : UserControl
{
    private readonly Lazy<AppShellService> _shell;
    private bool _recordingHotkey;

    public SettingsPage(SettingsViewModel viewModel, Lazy<AppShellService> shell)
    {
        ViewModel = viewModel;
        _shell = shell;
        InitializeComponent();
        DataContext = viewModel;
        WaylandHotkeyNote.IsVisible = DesktopSession.IsWayland;

        // Mica and Acrylic are Windows materials; elsewhere the choice would change nothing.
        BackdropCard.IsVisible = BackdropDivider.IsVisible = OperatingSystem.IsWindows();
        if (OperatingSystem.IsMacOS()) HotkeyCard.Description = Tr.Get("Settings_HotkeyHintMac");

        // Tunnel: the button must not turn Space or Enter into a click while recording.
        HotkeyButton.Click += (_, _) => StartRecording();
        HotkeyButton.AddHandler(KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);
        HotkeyButton.LostFocus += (_, _) => StopRecording();
    }

    public SettingsViewModel ViewModel { get; }

    private void OnRestartClick(object? sender, RoutedEventArgs e) => _shell.Value.Restart();

    private void OnAccentCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, DataContext: AccentOption option } && ViewModel.SelectedAccent != option)
            ViewModel.SelectedAccent = option;
    }

    private void StartRecording()
    {
        _recordingHotkey = true;
        ShowPrompt(true);
        HotkeyButton.Focus();
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopRecording();
            return;
        }

        // Modifier keys alone keep the recorder waiting for a main key.
        if (HotkeyCapture.TryCapture(e.Key, e.KeyModifiers) is not { } gesture) return;

        ViewModel.Hotkey = gesture;
        StopRecording();
    }

    private void StopRecording()
    {
        _recordingHotkey = false;
        ShowPrompt(false);
    }

    private void ShowPrompt(bool recording)
    {
        HotkeyPrompt.IsVisible = recording;
        HotkeyText.IsVisible = !recording;
    }

    private void OnHotkeyAlternativeClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Option<HotkeyGesture> option)
            ViewModel.ApplyHotkeyAlternative(option.Value);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => _shell.Value.Exit();

    private void OnCheckUpdatesClick(object? sender, RoutedEventArgs e) => UiTask.Run(() => OnCheckUpdatesClickAsync(sender), "Checking for updates");

    private async Task OnCheckUpdatesClickAsync(object? sender) => await ViewModel.CheckForUpdatesAsync();

    private void OnPasswordClick(object? sender, RoutedEventArgs e) => UiTask.Run(() => OnPasswordClickAsync(sender), "Changing the lock password");

    private async Task OnPasswordClickAsync(object? sender)
    {
        var password = ViewModel.HasLockPassword
            ? await PasswordPrompt.ChangeAsync(this, ViewModel.VerifyLockPassword, () => ViewModel.LockPasswordRetryAfter)
            : await PasswordPrompt.SetAsync(this);
        if (password is not null) await ViewModel.SetLockPasswordAsync(password);
    }

    private void OnRemovePasswordClick(object? sender, RoutedEventArgs e) => UiTask.Run(() => OnRemovePasswordClickAsync(sender), "Removing the lock password");

    private async Task OnRemovePasswordClickAsync(object? sender)
    {
        if (await PasswordPrompt.ConfirmRemoveAsync(this, ViewModel.VerifyLockPassword, () => ViewModel.LockPasswordRetryAfter))
            await ViewModel.RemoveLockPasswordAsync();
    }

    private void OnCategoryActionClick(object? sender, RoutedEventArgs e) => UiTask.Run(() => OnCategoryActionClickAsync(sender), "A category action");

    private async Task OnCategoryActionClickAsync(object? sender)
    {
        if (sender is not Button { DataContext: CategoryRow row } button) return;

        if (button.Classes.Contains("lock"))
        {
            // Locking needs the shared password first; there is nothing to ask if one is set.
            if (!ViewModel.HasLockPassword)
            {
                if (await PasswordPrompt.SetAsync(this) is not { } created) return;
                await ViewModel.SetLockPasswordAsync(created);
            }

            await ViewModel.LockCategoryAsync(row.Id);
        }
        else if (button.Classes.Contains("locknow"))
        {
            await ViewModel.LockCategoryAsync(row.Id);
        }
        else if (button.Classes.Contains("unlock"))
        {
            if (await PasswordPrompt.VerifyAsync(this, ViewModel.VerifyLockPassword, () => ViewModel.LockPasswordRetryAfter))
                await ViewModel.UnlockCategoryAsync(row.Id);
        }
        else if (button.Classes.Contains("rename"))
        {
            await RenameCategoryAsync(row);
        }
        else if (button.Classes.Contains("delete"))
        {
            await DeleteCategoryAsync(row);
        }
    }

    // A taken name keeps the dialog open with the reason under the box.
    private Task RenameCategoryAsync(CategoryRow row)
    {
        var box = new TextBox { Text = row.Name, [Avalonia.Automation.AutomationProperties.NameProperty] = Tr.Get("Category_RenameTitle") };
        var dialog = new InlineDialog(Tr.Get("Category_RenameTitle"), Tr.Get("Common_Save"), box);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) dialog.IsPrimaryEnabled = !string.IsNullOrWhiteSpace(box.Text);
        };
        dialog.Accept = async () =>
        {
            if (await ViewModel.RenameCategoryAsync(row.Id, box.Text ?? string.Empty)) return true;
            dialog.ShowError(Tr.Get("Category_Exists"));
            return false;
        };
        return dialog.ShowAsync(this);
    }

    // Deleting a locked category leaves its clips in the open history, so it needs the password,
    // even while the category is unlocked.
    private async Task DeleteCategoryAsync(CategoryRow row)
    {
        if (row.IsLocked && ViewModel.HasLockPassword &&
            !await PasswordPrompt.VerifyAsync(this, ViewModel.VerifyLockPassword, () => ViewModel.LockPasswordRetryAfter))
            return;

        var message = new TextBlock { Text = Tr.Format("Category_DeleteConfirm", row.Name), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        if (await new InlineDialog(Tr.Get("Category_DeleteTitle"), Tr.Get("Common_Delete"), message).ShowAsync(this))
            await ViewModel.DeleteCategoryAsync(row.Id);
    }

    private void OnExportClick(object? sender, RoutedEventArgs e) => UiTask.Run(() => OnExportClickAsync(sender), "Exporting");

    private async Task OnExportClickAsync(object? sender)
    {
        if (ViewModel.HasLockPassword && !await PasswordPrompt.VerifyAsync(this, ViewModel.VerifyLockPassword, () => ViewModel.LockPasswordRetryAfter)) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;

        var csv = ViewModel.SelectedExportFormat.Value == ClipExportFormat.Csv;
        var extension = csv ? "csv" : "json";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = ViewModel.SuggestedExportFileName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(Tr.Get(csv ? "Export_Csv" : "Export_Json")) { Patterns = [$"*.{extension}"] }],
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Documents),
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        await ViewModel.ExportAsync(path);
    }

    public Task LoadAsync() => Task.WhenAll(ViewModel.RefreshCategoriesAsync(), ViewModel.CheckForUpdatesOnceAsync());
}
