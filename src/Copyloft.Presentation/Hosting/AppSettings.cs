using System.Text.Json.Serialization;
using Copyloft.Core.Input;
using Copyloft.Core.Theming;

namespace Copyloft.Presentation.Hosting;

// Saved as settings.json. Changing Language takes effect after a restart.
public sealed record AppSettings
{
    public static HotkeyGesture DefaultHotkey { get; } =
        new(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x56); // V

    public string Language { get; init; } = "en-US";

    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; init; } = AppTheme.System;

    // #RRGGBB accent override; null follows the Windows accent. Takes effect after a restart.
    public string? Accent { get; init; }

    // Window backdrop, from WinUI 3.
    [JsonConverter(typeof(JsonStringEnumConverter<BackdropKind>))]
    public BackdropKind Backdrop { get; init; } = BackdropKind.Mica;

    // How many uncategorized clips to keep; pinned and categorized clips are never pruned.
    public int MaxItems { get; init; } = 100;

    // Days to keep uncategorized clips; 0 keeps them forever. Pinned and categorized clips are never pruned.
    public int RetentionDays { get; init; }

    // When true, unpinned clips are cleared when the user signs out or shuts down.
    public bool ClearOnSignOut { get; init; }

    public bool RunAtStartup { get; init; }

    // Global shortcut that opens the flyout; at least one modifier plus one key.
    [JsonConverter(typeof(HotkeyGestureJsonConverter))]
    public HotkeyGesture Hotkey { get; init; } = DefaultHotkey;

    // When true, double-clicking the tray icon opens the full history window.
    public bool OpenHistoryOnDoubleClick { get; init; } = true;

    // Read once to move an older version's password hash into the database, then always null.
    public string? LockPasswordHash { get; init; }

    // When true, unlocked categories lock again on the matching event.
    public bool LockOnExit { get; init; } = true;

    public bool LockOnMinimize { get; init; }

    public bool LockOnShutdown { get; init; } = true;

    // Left by older versions; startup clears it without trusting it (the list is in the database).
    public long[] UnlockedCategories { get; init; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter<TrayIconKind>))]
    public TrayIconKind TrayIcon { get; init; } = TrayIconKind.Filled;

    public bool ShowTrayIcon { get; init; } = true;

    // When true, the first-run welcome window has already been shown.
    public bool WelcomeShown { get; init; }
}
