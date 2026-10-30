using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Copyloft.Core.Models;
using Copyloft.Desktop.Controls;
using Copyloft.Presentation.Hosting;
using NSubstitute;

namespace Copyloft.Desktop.Tests;

public sealed class SettingsDialogsTests
{
    private static async Task<(SettingsKit Kit, Window Window)> Open(bool passwordSet, params Category[] categories)
    {
        var kit = new SettingsKit(passwordSet: passwordSet);
        kit.Passwords.Verify("1234").Returns(true);
        kit.StoredCategories.AddRange(categories);
        var window = new Window { Content = kit.Page, Width = 900, Height = 700 };
        window.Show();
        await kit.Page.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        return (kit, window);
    }

    private static Button RowButton(SettingsKit kit, string kind) =>
        kit.Page.FindControl<ItemsControl>("CategoryList")!.GetVisualDescendants()
            .OfType<Button>().Single(b => b.Classes.Contains(kind));

    private static void Fill(Window window, params string[] values)
    {
        var dialog = DialogDriver.Open(window);
        var boxes = DialogDriver.Boxes(dialog);
        for (var i = 0; i < values.Length; i++) boxes[i].Text = values[i];
        DialogDriver.Click(dialog.FindControl<Button>("PrimaryButton")!);
    }

    [Fact]
    public Task Set_password_saves_the_new_one() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: false);
        Assert.False(kit.Page.FindControl<Control>("RemovePasswordButton")!.IsVisible);

        DialogDriver.Click(kit.Page.FindControl<Button>("PasswordButton")!);
        Fill(window, "abcd", "abcd");

        kit.Passwords.Received().Set("abcd");
    });

    [Fact]
    public Task Change_password_needs_the_current_one() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: true);

        DialogDriver.Click(kit.Page.FindControl<Button>("PasswordButton")!);
        Fill(window, "1234", "wxyz", "wxyz");

        kit.Passwords.Received().Set("wxyz");
    });

    [Fact]
    public Task Remove_password_needs_the_current_one() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: true);

        DialogDriver.Click(kit.Page.FindControl<Button>("RemovePasswordButton")!);
        Fill(window, "1234");

        kit.Passwords.Received().Remove();
    });

    [Fact]
    public Task Locking_without_a_password_sets_one_first() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: false, new Category(3, "Bank"));

        DialogDriver.Click(RowButton(kit, "lock"));
        Fill(window, "abcd", "abcd");

        kit.Passwords.Received().Set("abcd");
        await kit.Categories.Received().SetLockedAsync(3, true, Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task Unlock_asks_for_the_password() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: true, new Category(3, "Bank", IsLocked: true));

        DialogDriver.Click(RowButton(kit, "unlock"));
        Fill(window, "1234");

        kit.Locks.Received().Unlock(3);
    });

    [Fact]
    public Task Rename_stays_open_on_a_taken_name() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: false, new Category(3, "Bank"));
        kit.Categories.RenameAsync(3, "Work", Arg.Any<CancellationToken>()).Returns(false);
        kit.Categories.RenameAsync(3, "Money", Arg.Any<CancellationToken>()).Returns(true);

        DialogDriver.Click(RowButton(kit, "rename"));
        Assert.Equal("Bank", DialogDriver.Boxes(DialogDriver.Open(window))[0].Text);
        Fill(window, "Work");
        var dialog = InlineDialog.OpenIn(window);
        Assert.NotNull(dialog);
        Assert.True(dialog.FindControl<TextBlock>("ErrorText")!.IsVisible);

        Fill(window, "Money");
        Assert.Null(InlineDialog.OpenIn(window));
    });

    [Fact]
    public Task Delete_asks_before_deleting() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: false, new Category(3, "Bank"));

        DialogDriver.Click(RowButton(kit, "delete"));
        await kit.Categories.DidNotReceive().DeleteAsync(3, Arg.Any<CancellationToken>());
        Fill(window);

        await kit.Categories.Received().DeleteAsync(3, Arg.Any<CancellationToken>());
    });

    // Deleting a locked category frees its clips into the open history.
    [Fact]
    public Task Locked_delete_needs_password() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: true, new Category(3, "Bank", IsLocked: true));

        DialogDriver.Click(RowButton(kit, "delete"));
        Fill(window, "wrong");
        Assert.NotNull(InlineDialog.OpenIn(window));
        Fill(window, "1234");
        Dispatcher.UIThread.RunJobs();
        await kit.Categories.DidNotReceive().DeleteAsync(3, Arg.Any<CancellationToken>());
        Fill(window);

        await kit.Categories.Received().DeleteAsync(3, Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task Export_with_a_password_asks_for_it_first() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: true);
        var export = kit.Page.FindControl<Button>("ExportButton")!;
        Assert.True(export.IsEnabled);

        DialogDriver.Click(export);

        Assert.Equal("Password", DialogDriver.Open(window).FindControl<TextBlock>("TitleText")!.Text);
    });

    // A failing store must not take the app down: the click handlers run detached.
    [Fact]
    public Task A_failing_delete_is_logged_not_thrown() => Ui.Run(async () =>
    {
        var (kit, window) = await Open(passwordSet: false, new Category(3, "Bank"));
        kit.Categories.DeleteAsync(3, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("database is locked")));

        DialogDriver.Click(RowButton(kit, "delete"));
        Fill(window);
        for (var i = 0; i < 3; i++) { await Task.Yield(); Dispatcher.UIThread.RunJobs(); }

        await kit.Categories.Received().DeleteAsync(3, Arg.Any<CancellationToken>());
    });
}
