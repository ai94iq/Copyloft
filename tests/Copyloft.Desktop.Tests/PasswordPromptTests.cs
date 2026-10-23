using Avalonia.Controls;
using Copyloft.Desktop.Controls;
using Copyloft.Desktop.Features;

namespace Copyloft.Desktop.Tests;

public sealed class PasswordPromptTests
{
    private static bool Verify(string password) => password == "1234";

    private static void Accept(InlineDialog dialog, params string[] passwords)
    {
        var boxes = DialogDriver.Boxes(dialog);
        for (var i = 0; i < passwords.Length; i++) boxes[i].Text = passwords[i];
        DialogDriver.Click(dialog.FindControl<Button>("PrimaryButton")!);
    }

    private static string? Error(InlineDialog dialog) =>
        dialog.FindControl<TextBlock>("ErrorText") is { IsVisible: true } error ? error.Text : null;

    [Fact]
    public Task Verify_asks_again_after_a_wrong_password() => Ui.Run(async () =>
    {
        var window = DialogDriver.Window();
        var result = PasswordPrompt.VerifyAsync(window, Verify);
        var dialog = DialogDriver.Open(window);

        Accept(dialog, "nope");
        Assert.Equal("That password is not right.", Error(dialog));
        Assert.False(result.IsCompleted);

        Accept(dialog, "1234");
        Assert.True(await result.WaitAsync(TimeSpan.FromSeconds(5)));
    });

    [Fact]
    public Task During_the_wait_after_wrong_tries_the_error_says_how_long() => Ui.Run(() =>
    {
        var window = DialogDriver.Window();
        var result = PasswordPrompt.VerifyAsync(window, _ => false, () => TimeSpan.FromSeconds(29.2));
        var dialog = DialogDriver.Open(window);

        Accept(dialog, "1234");

        Assert.Equal("Too many wrong tries. Try again in 30 seconds.", Error(dialog));
        Assert.False(result.IsCompleted);
    });

    [Fact]
    public Task Password_boxes_hide_what_is_typed() => Ui.Run(() =>
    {
        var window = DialogDriver.Window();
        _ = PasswordPrompt.SetAsync(window);

        Assert.All(DialogDriver.Boxes(DialogDriver.Open(window)), box => Assert.NotEqual(default, box.PasswordChar));
    });

    [Theory]
    [InlineData("12", "12", "Use at least 4 characters.")]
    [InlineData("12345", "54321", "The passwords don't match.")]
    public Task Set_rejects_a_weak_or_mismatched_password(string password, string confirm, string error) => Ui.Run(() =>
    {
        var window = DialogDriver.Window();
        var result = PasswordPrompt.SetAsync(window);
        var dialog = DialogDriver.Open(window);

        Accept(dialog, password, confirm);

        Assert.Equal(error, Error(dialog));
        Assert.False(result.IsCompleted);
    });

    [Fact]
    public Task Set_returns_the_new_password() => Ui.Run(async () =>
    {
        var window = DialogDriver.Window();
        var result = PasswordPrompt.SetAsync(window);

        Accept(DialogDriver.Open(window), "abcd", "abcd");

        Assert.Equal("abcd", await result.WaitAsync(TimeSpan.FromSeconds(5)));
    });

    [Fact]
    public Task Change_needs_the_current_password() => Ui.Run(async () =>
    {
        var window = DialogDriver.Window();
        var result = PasswordPrompt.ChangeAsync(window, Verify);
        var dialog = DialogDriver.Open(window);

        Accept(dialog, "nope", "abcd", "abcd");
        Assert.Equal("That password is not right.", Error(dialog));

        Accept(dialog, "1234", "abcd", "abcd");
        Assert.Equal("abcd", await result.WaitAsync(TimeSpan.FromSeconds(5)));
    });

    [Fact]
    public Task Remove_needs_the_current_password() => Ui.Run(async () =>
    {
        var window = DialogDriver.Window();
        var result = PasswordPrompt.ConfirmRemoveAsync(window, Verify);
        var dialog = DialogDriver.Open(window);

        Accept(dialog, "nope");
        Assert.False(result.IsCompleted);

        Accept(dialog, "1234");
        Assert.True(await result.WaitAsync(TimeSpan.FromSeconds(5)));
    });

    [Fact]
    public Task Cancelling_set_returns_nothing() => Ui.Run(async () =>
    {
        var window = DialogDriver.Window();
        var result = PasswordPrompt.SetAsync(window);

        DialogDriver.Click(DialogDriver.Open(window).FindControl<Button>("CancelButton")!);

        Assert.Null(await result.WaitAsync(TimeSpan.FromSeconds(5)));
    });
}
