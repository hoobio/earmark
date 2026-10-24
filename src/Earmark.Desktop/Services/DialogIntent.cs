using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using FluentAvalonia.UI.Controls;

namespace Earmark.Desktop.Services;

// Avalonia's Button.OnKeyUp clicks on a Space key-up with no key-down, so a key held over from before the dialog opened could confirm it; Primary and Secondary count only from a deliberate press.
internal sealed class DialogIntent
{
    private readonly FAContentDialog _dialog;
    private readonly TopLevel _owner;
    private readonly HashSet<Key> _held = [];
    private readonly HashSet<Key> _down = [];
    private bool _opened;
    private Button? _pressed;
    private bool _activating;
    private FAContentDialogResult? _chosen;

    public DialogIntent(FAContentDialog dialog, TopLevel owner)
    {
        _dialog = dialog;
        _owner = owner;
        _dialog.AddHandler(InputElement.KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _dialog.AddHandler(InputElement.KeyDownEvent, OnInputDone, RoutingStrategies.Bubble, handledEventsToo: true);
        _dialog.AddHandler(InputElement.KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _dialog.AddHandler(InputElement.KeyUpEvent, OnInputDone, RoutingStrategies.Bubble, handledEventsToo: true);
        _dialog.AddHandler(InputElement.PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _dialog.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _dialog.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleasedDone, RoutingStrategies.Bubble, handledEventsToo: true);
        _dialog.Opened += OnOpened;
        _dialog.PrimaryButtonClick += OnPrimaryButtonClick;
        _dialog.SecondaryButtonClick += OnSecondaryButtonClick;
        if (_owner is WindowBase window)
        {
            window.Activated += OnOwnerActivated;
        }
    }

    public FAContentDialogResult Result(FAContentDialogResult result) =>
        result is FAContentDialogResult.Primary or FAContentDialogResult.Secondary && result != _chosen ? FAContentDialogResult.None : result;

    public void Detach()
    {
        _dialog.RemoveHandler(InputElement.KeyDownEvent, OnKeyDownTunnel);
        _dialog.RemoveHandler(InputElement.KeyDownEvent, OnInputDone);
        _dialog.RemoveHandler(InputElement.KeyUpEvent, OnKeyUpTunnel);
        _dialog.RemoveHandler(InputElement.KeyUpEvent, OnInputDone);
        _dialog.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressedTunnel);
        _dialog.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleasedTunnel);
        _dialog.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleasedDone);
        _dialog.Opened -= OnOpened;
        _dialog.PrimaryButtonClick -= OnPrimaryButtonClick;
        _dialog.SecondaryButtonClick -= OnSecondaryButtonClick;
        if (_owner is WindowBase window)
        {
            window.Activated -= OnOwnerActivated;
        }
    }

    private static bool IsActivationKey(Key key) => key is Key.Enter or Key.Space;

    private bool IsOpen => _opened && _dialog.IsEffectivelyVisible;

    private bool IsActive => _owner is not WindowBase window || window.IsActive;

    private void SnapshotHeldKeys()
    {
        _held.Clear();
        foreach (var key in new[] { Key.Enter, Key.Space }.Where(AppDialogs.IsKeyHeld))
        {
            _held.Add(key);
        }
    }

    private void OnOpened(FAContentDialog sender, EventArgs args)
    {
        _opened = true;
        SnapshotHeldKeys();
    }

    private void OnOwnerActivated(object? sender, EventArgs e) => SnapshotHeldKeys();

    // Enter on any element reaches FluentAvalonia's default-button path, so it is gated wherever focus is; Space only on the dialog's own buttons.
    private bool CanActivate(Key key, object? source) => key == Key.Enter || GatedButton(source) is not null;

    private Button? GatedButton(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(button =>
            button.Name is "PrimaryButton" or "SecondaryButton" && _dialog.IsVisualAncestorOf(button));

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!IsActivationKey(e.Key))
        {
            return;
        }

        if (IsOpen && IsActive && !_held.Contains(e.Key) && _down.Add(e.Key))
        {
            _activating = true;
            return;
        }

        if (CanActivate(e.Key, e.Source))
        {
            e.Handled = true;
        }
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (!IsActivationKey(e.Key))
        {
            return;
        }

        _held.Remove(e.Key);
        if (_down.Remove(e.Key) && IsOpen && IsActive)
        {
            _activating = true;
            return;
        }

        if (CanActivate(e.Key, e.Source))
        {
            e.Handled = true;
        }
    }

    private void OnInputDone(object? sender, KeyEventArgs e) => _activating = false;

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (GatedButton(e.Source) is not { } button)
        {
            return;
        }

        _pressed = IsOpen && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed ? button : null;
        if (_pressed is null)
        {
            e.Handled = true;
        }
    }

    private void OnPointerReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        if (GatedButton(e.Source) is not { } button)
        {
            return;
        }

        if (_pressed == button && IsOpen)
        {
            _activating = true;
            return;
        }

        e.Handled = true;
    }

    private void OnPointerReleasedDone(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;
        _activating = false;
    }

    private void OnPrimaryButtonClick(FAContentDialog sender, FAContentDialogButtonClickEventArgs args) => Choose(FAContentDialogResult.Primary, "PrimaryButton", args);

    private void OnSecondaryButtonClick(FAContentDialog sender, FAContentDialogButtonClickEventArgs args) => Choose(FAContentDialogResult.Secondary, "SecondaryButton", args);

    private bool IsAutomationInvoke(string name) =>
        IsOpen && _dialog.GetVisualDescendants().OfType<DialogCommandButton>().Any(button => button.Name == name && button.IsAutomationInvoke);

    private void Choose(FAContentDialogResult result, string name, FAContentDialogButtonClickEventArgs args)
    {
        if (_activating || IsAutomationInvoke(name))
        {
            _chosen = result;
        }
        else
        {
            args.Cancel = true;
        }
    }
}
