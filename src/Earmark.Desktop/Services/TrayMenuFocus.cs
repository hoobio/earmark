using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Earmark.Desktop.Services;

public static class TrayMenuFocus
{
    private static bool _attached;

    // Avalonia's Win32 TrayIconImpl.OnRightClicked shows the menu presenter in a window without focusing it, so Escape and the arrow keys stop at the window and never reach the menu's key handler.
    public static void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (window.Name?.StartsWith("AvaloniaTrayPopupRoot_", StringComparison.Ordinal) == true && window.Content is MenuFlyoutPresenter presenter)
            {
                presenter.Focusable = true;
                window.Activated += (_, _) => Dispatcher.UIThread.Post(() => presenter.Focus());
                // MoveSelection focuses the item with NavigationMethod.Unspecified, so it never gets :focus-visible and the selected-item style draws no highlight.
                presenter.AddHandler(InputElement.KeyDownEvent, (_, e) =>
                {
                    if (e.Key is Key.Up or Key.Down or Key.Home or Key.End)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (presenter.ContainerFromIndex(presenter.SelectedIndex) is { } item)
                            {
                                presenter.Focus();
                                item.Focus(NavigationMethod.Directional);
                            }
                        });
                    }
                }, RoutingStrategies.Bubble, handledEventsToo: true);
            }
        });
    }
}
