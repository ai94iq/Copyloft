using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Copyloft.Desktop.Controls;
using Copyloft.Desktop.Platform;

namespace Copyloft.Desktop.Shell;

// The borderless, topmost flyout next to the pointer. It hides when it loses focus or on Esc,
// and hands the foreground back to the app the user came from.
public sealed partial class FlyoutWindow : Window
{
    private const int WidthDip = 360;
    private const int HeightDip = 480;
    private const int MarginDip = 12;
    private const int CursorGapDip = 8;

    // A tray click deactivates the open flyout (hiding it) just before it toggles; a toggle this
    // soon after such a hide is that same click and must not reopen it.
    private static readonly TimeSpan DeactivationEcho = TimeSpan.FromMilliseconds(300);

    private readonly IFlyoutPlatform _platform;
    private readonly IPasteBack _pasteBack;
    private readonly TimeProvider _time;
    private IntPtr _previousWindow;
    private DateTimeOffset? _hiddenByDeactivationAt;

    public FlyoutWindow(
        ClipboardPanelViewModel viewModel, IFlyoutPlatform platform, IPasteBack pasteBack, TimeProvider time)
    {
        ViewModel = viewModel;
        _platform = platform;
        _pasteBack = pasteBack;
        _time = time;
        InitializeComponent();
        WindowSurface.Attach(this, "CopyloftCardBrush", flyout: true);
        DataContext = viewModel;
        FlowDirection = Culture.IsRtl ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        Deactivated += (_, _) => HideAfterDeactivation();

        // Tunnel, so the flyout sees its keys before whichever control has focus.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ClipList.AddHandler(PointerReleasedEvent, OnListClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        viewModel.ItemActivated += (_, _) => PasteBack();
        viewModel.SelectionCopied += (_, _) => HideFlyout();
        viewModel.CategoryMenuRequested += item => _ = CategoryMenu.ShowAsync(CategoryMenu.Anchor(ClipList, item), item, viewModel);
    }

    public ClipboardPanelViewModel ViewModel { get; }

    public bool IsOpen { get; private set; }

    internal IntPtr PreviousWindow => _previousWindow;

    // Pixel bounds next to the pointer, pushed back inside the work area at the edges.
    public static PixelRect Place(PixelPoint cursor, PixelRect workArea, double scaling)
    {
        int Px(int dip) => (int)(dip * scaling);
        var (x, y) = PanelPlacement.NearCursor(
            cursor.X, cursor.Y, Px(WidthDip), Px(HeightDip),
            workArea.X, workArea.Y, workArea.Right, workArea.Bottom, Px(MarginDip), Px(CursorGapDip));
        return new PixelRect(x, y, Px(WidthDip), Px(HeightDip));
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            HideFlyout();
            return;
        }

        if (_hiddenByDeactivationAt is { } hiddenAt && _time.GetUtcNow() - hiddenAt < DeactivationEcho)
        {
            _hiddenByDeactivationAt = null;
            return;
        }

        ShowFlyout();
    }

    public void ShowFlyout()
    {
        ViewModel.ResetTransientState();
        ViewModel.IsViewVisible = true;
        _previousWindow = _platform.ForegroundWindow();
        _hiddenByDeactivationAt = null;
        IsOpen = true;

        var cursor = _platform.Cursor();
        if ((Screens.ScreenFromPoint(cursor) ?? Screens.Primary) is { } screen)
            Position = Place(cursor, screen.WorkingArea, screen.Scaling).Position;

        Show();
        Activate();
        SearchBox.Focus();
        _platform.ForceForeground(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        // Refresh after the window is on screen so opening is never delayed; the quiet refresh
        // keeps the previous list visible instead of showing a spinner.
        _ = ViewModel.RefreshQuietAsync();
    }

    public void HideFlyout()
    {
        if (!IsOpen) return;

        IsOpen = false;
        ViewModel.IsViewVisible = false;
        _platform.RestoreForeground(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, _previousWindow);
        Hide();
    }

    internal PasteHint? Hint { get; private set; }

    // The pick went to the clipboard; close and paste into the window the user came from, or,
    // where no keystroke can reach it, say how to paste.
    private void PasteBack()
    {
        // An image pick finishes after its load and write; if the user dismissed the flyout in
        // the meantime, they have moved on and no keystroke may follow them.
        if (!IsOpen) return;

        var target = _previousWindow;
        var position = Position;
        HideFlyout();
        if (_pasteBack.CanPaste)
        {
            _ = _pasteBack.IntoAsync(target);
            return;
        }

        Hint ??= new PasteHint();
        Hint.ShowAt(position);
    }

    // Arrows move the selection, Enter picks it, Ctrl+1..9 pick a row, Esc steps back.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (InlineDialog.IsOpenIn(this)) return;     // Enter and Esc belong to the dialog

        switch (e.Key)
        {
            case Key.Down:
                ViewModel.MoveSelection(1);
                break;
            case Key.Up:
                ViewModel.MoveSelection(-1);
                break;
            case Key.Enter:
                ViewModel.ActivateSelected();
                break;
            case Key.Escape:
                // Leave select mode first, then cancel the confirmation, and only then close.
                if (!ViewModel.DismissTransient()) HideFlyout();
                break;
            case >= Key.D1 and <= Key.D9 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                var index = e.Key - Key.D1;
                if (index < ViewModel.Items.Count) ViewModel.Activate(ViewModel.Items[index]);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // A click on a row picks it; clicks on the row buttons stay with those buttons.
    private void OnListClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source) return;
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is ClipItemViewModel item)
            ViewModel.Activate(item);
    }

    internal void HideAfterDeactivation()
    {
        if (!IsOpen) return;

        HideFlyout();
        _hiddenByDeactivationAt = _time.GetUtcNow();
    }
}
