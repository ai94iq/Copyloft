using Avalonia;
using Avalonia.Controls;
using Copyloft.Desktop.Controls;

namespace Copyloft.Desktop.Features;

// The shared lock password: set, change, remove, and verify before exporting or unlocking.
internal static class PasswordPrompt
{
    private const int MinimumLength = 4;

    // Asks again until the password verifies, or the user cancels.
    public static Task<bool> VerifyAsync(Visual anchor, Func<string, bool> verify, Func<TimeSpan>? retryAfter = null)
    {
        var box = PasswordBox("Password_Current");
        var dialog = new InlineDialog(Tr.Get("Password_VerifyTitle"), Tr.Get("Common_Ok"), box);
        dialog.Accept = () => Task.FromResult(CheckPassword(dialog, verify, Text(box), retryAfter));
        return dialog.ShowAsync(anchor);
    }

    // Setting a password for the first time; returns it, or null when cancelled.
    public static async Task<string?> SetAsync(Visual anchor)
    {
        var password = PasswordBox("Password_New");
        var confirm = PasswordBox("Password_Confirm");
        var dialog = new InlineDialog(Tr.Get("Password_SetTitle"), Tr.Get("Common_Save"), password, confirm);
        dialog.Accept = () => Task.FromResult(TryCreate(dialog, Text(password), Text(confirm)));
        return await dialog.ShowAsync(anchor) ? Text(password) : null;
    }

    // Changing: the current password must verify first.
    public static async Task<string?> ChangeAsync(Visual anchor, Func<string, bool> verify, Func<TimeSpan>? retryAfter = null)
    {
        var current = PasswordBox("Password_Current");
        var password = PasswordBox("Password_New");
        var confirm = PasswordBox("Password_Confirm");
        var dialog = new InlineDialog(Tr.Get("Password_ChangeTitle"), Tr.Get("Common_Save"), current, password, confirm);
        dialog.Accept = () => Task.FromResult(
            CheckPassword(dialog, verify, Text(current), retryAfter) && TryCreate(dialog, Text(password), Text(confirm)));
        return await dialog.ShowAsync(anchor) ? Text(password) : null;
    }

    // Removing: the current password is still required; the warning spells out the consequence.
    public static Task<bool> ConfirmRemoveAsync(Visual anchor, Func<string, bool> verify, Func<TimeSpan>? retryAfter = null)
    {
        var warning = new TextBlock { Text = Tr.Get("Password_RemoveWarning"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var current = PasswordBox("Password_Current");
        var dialog = new InlineDialog(Tr.Get("Password_RemoveTitle"), Tr.Get("Common_Remove"), warning, current);
        dialog.Accept = () => Task.FromResult(CheckPassword(dialog, verify, Text(current), retryAfter));
        return dialog.ShowAsync(anchor);
    }

    private static bool TryCreate(InlineDialog dialog, string password, string confirm) =>
        Check(dialog, password.Length >= MinimumLength, "Password_TooShort") &&
        Check(dialog, password == confirm, "Password_Mismatch");

    // A wrong password, or any password during the wait after too many wrong ones, keeps the
    // dialog open; the wait says how long it has left.
    private static bool CheckPassword(InlineDialog dialog, Func<string, bool> verify, string password, Func<TimeSpan>? retryAfter)
    {
        if (verify(password)) return true;

        var wait = retryAfter?.Invoke() ?? TimeSpan.Zero;
        dialog.ShowError(wait > TimeSpan.Zero
            ? Tr.Format("Password_Wait", (int)Math.Ceiling(wait.TotalSeconds))
            : Tr.Get("Password_Wrong"));
        return false;
    }

    private static bool Check(InlineDialog dialog, bool ok, string errorKey)
    {
        if (!ok) dialog.ShowError(Tr.Get(errorKey));
        return ok;
    }

    private static string Text(TextBox box) => box.Text ?? string.Empty;

    private static TextBox PasswordBox(string placeholderKey) => new()
    {
        PasswordChar = '●',
        Classes = { "revealPasswordButton" },
        PlaceholderText = Tr.Get(placeholderKey),
        [Avalonia.Automation.AutomationProperties.NameProperty] = Tr.Get(placeholderKey),
    };
}
