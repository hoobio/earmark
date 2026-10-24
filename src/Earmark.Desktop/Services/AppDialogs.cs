using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Earmark.Desktop.Views;

using FluentAvalonia.UI.Controls;

namespace Earmark.Desktop.Services;

public static class AppDialogs
{
    private static Task _current = Task.CompletedTask;

    private static bool _openFadeShortened;

    public static Func<Key, bool> IsKeyHeld { get; set; } = Native.IsKeyHeld;

    public static async Task<bool> ShowMessageAsync(TopLevel owner, string title, string body, string? primary, string close)
    {
        var dialog = new FAContentDialog
        {
            Title = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary ?? string.Empty,
            CloseButtonText = close,
            DefaultButton = primary is null ? FAContentDialogButton.Close : FAContentDialogButton.None,
        };
        return await ShowAsync(dialog, owner) == FAContentDialogResult.Primary;
    }

    public static async Task<FAContentDialogResult> ShowAsync(FAContentDialog dialog, TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(owner);

        var previous = _current;
        var done = new TaskCompletionSource();
        _current = done.Task;
        var dismissed = false;
        DialogIntent? intent = null;
        // FluentAvalonia's close path throws on a dialog that was never shown, so a Hide() while it waits here is cancelled and remembered.
        void CancelWhileQueued(FAContentDialog sender, FAContentDialogClosingEventArgs args)
        {
            args.Cancel = true;
            dismissed = true;
        }

        dialog.Closing += CancelWhileQueued;
        try
        {
            await previous.ConfigureAwait(true);
            if (owner is MainWindow main)
            {
                await main.ForegroundGate.WhenReleased.ConfigureAwait(true);
                // WinUI centres the panel below the 1px top border and rounds a half pixel up, where Avalonia rounds it to even.
                var shellTop = main.Root.Margin.Top > 0 ? main.Root.Margin.Top + 0.01 : 0;
                dialog.TemplateApplied += (_, e) =>
                {
                    if (e.NameScope.Find<Border>("BackgroundElement") is { } panel)
                    {
                        panel.Margin = new Thickness(0, shellTop, 0, 0);
                    }
                };
            }

            if (dismissed)
            {
                return FAContentDialogResult.None;
            }

            dialog.Closing -= CancelWhileQueued;
            ShortenOpenFade();
            dialog.Classes.Add("Earmark");
            // The app's ButtonBackgroundPointerOver matches ContentDialogBackground, so a hovered Cancel would vanish into the dialog.
            dialog.Resources["ButtonBackgroundPointerOver"] = Resource("DialogButtonBackgroundPointerOver");
            dialog.Resources["ButtonBackgroundPressed"] = Resource("DialogButtonBackgroundPressed");
            if (Resource("EarmarkDialogResources") is IResourceDictionary overrides)
            {
                foreach (var (key, value) in overrides)
                {
                    dialog.Resources[key] = value;
                }
            }

            dialog.Opened += (_, _) =>
            {
                AccentPrimaryButton(dialog);
                Dispatcher.UIThread.Post(() => FocusFirstInput(dialog));
            };
            HideFlyouts(owner);
            // FluentAvalonia 3.1.0's close path turns hit testing off and its show path never turns it back on, so a re-shown dialog would let clicks through.
            dialog.IsHitTestVisible = true;
            // The close fade leaves LayoutRoot's IsVisible false as a local value, which surfaces for a frame when the next open animation ends and drops focus out of the dialog.
            if (dialog.GetVisualDescendants().OfType<Panel>().FirstOrDefault(panel => panel.Name == "LayoutRoot") is { } layoutRoot)
            {
                layoutRoot.ClearValue(Visual.IsVisibleProperty);
            }

            intent = new DialogIntent(dialog, owner);
            return intent.Result(await dialog.ShowAsync(owner).ConfigureAwait(true));
        }
        finally
        {
            intent?.Detach();
            done.SetResult();
        }
    }

    public static void HideFlyouts(TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        foreach (var popup in owner.OpenedPopups.ToList())
        {
            popup.Close();
        }
    }

    // FluentAvalonia fades the dialog in over 250ms with its scale; WinUI fades it over ControlFasterAnimationDuration (83ms) and scales over 250ms, and a second app animation on the same Opacity fights the theme's.
    private static void ShortenOpenFade()
    {
        if (_openFadeShortened || Resource(typeof(FAContentDialog)) is not ControlTheme theme)
        {
            return;
        }

        _openFadeShortened = true;
        foreach (var animation in theme.Children.OfType<Style>().SelectMany(style => style.Animations).OfType<Animation>())
        {
            if (animation.Duration == TimeSpan.FromMilliseconds(250) && animation.Children.Any(frame => frame.Setters.OfType<Setter>().Any(setter => setter.Property == Visual.OpacityProperty)))
            {
                animation.Duration = TimeSpan.FromMilliseconds(83);
            }
        }
    }

    private static void AccentPrimaryButton(FAContentDialog dialog)
    {
        if (dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "PrimaryButton") is { Theme: null } primary
            && Resource("AccentButtonStyle") is ControlTheme accent)
        {
            primary.Theme = accent;
        }
    }

    internal static void FocusFirstInput(FAContentDialog dialog)
    {
        if (TopLevel.GetTopLevel(dialog)?.FocusManager is not { } focus)
        {
            return;
        }

        var content = dialog.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(presenter => presenter.Name == "Content");
        var first = content?.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(element => IsFocusTarget(element) && !IsCommandButton(element));
        if (first is not null)
        {
            first.Focus();
            return;
        }

        if (focus.GetFocusedElement() is Visual focused && (focused == dialog || dialog.IsVisualAncestorOf(focused)) && !IsUnsafeStart(dialog, focused))
        {
            return;
        }

        var targets = dialog.GetVisualDescendants().OfType<InputElement>().Where(IsFocusTarget).ToList();
        var fallback = targets.FirstOrDefault(element => element is Button { Name: "CloseButton" }) ?? targets.FirstOrDefault(element => !IsUnsafeStart(dialog, element));
        (fallback ?? dialog).Focus();
    }

    // Focus on a confirming button that is not the default would let a stray Enter or Space confirm, so a dialog starts on its Close button instead.
    private static bool IsUnsafeStart(FAContentDialog dialog, Visual element) =>
        element is Button { Name: "PrimaryButton" } && dialog.DefaultButton != FAContentDialogButton.Primary;

    private static bool IsCommandButton(InputElement element) => element is Button { Name: "PrimaryButton" or "SecondaryButton" or "CloseButton" };

    private static bool IsFocusTarget(InputElement element) =>
        element.Focusable && KeyboardNavigation.GetIsTabStop(element) && element.IsEffectivelyVisible && element.IsEffectivelyEnabled;

    private static object? Resource(object key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value : null;
}
