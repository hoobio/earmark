using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.System;

namespace Earmark.App.Views;

public sealed partial class HomePage : Page
{
    private readonly ILogger<HomePage>? _logger;
    private readonly RulesViewModel _rulesViewModel;
    private readonly MainWindow _mainWindow;

    /// <summary>
    /// Pre-drag volume / mute captured per slider. Indexed by the Slider instance because
    /// the same DeviceCard could theoretically host concurrent interactions; in practice this
    /// also dodges any "card replaced mid-drag" edge cases by keying off the live control.
    /// </summary>
    private readonly Dictionary<Slider, (float Volume, bool Muted)> _sliderDragStart = new();

    /// <summary>
    /// In-flight rules-expand storyboards keyed by ScrollViewer. Lets a mid-animation toggle
    /// stop the previous tween before starting the next so they don't fight over MaxHeight.
    /// </summary>
    private readonly Dictionary<ScrollViewer, Storyboard> _rulesExpandStoryboards = new();

    public HomePage(HomeViewModel viewModel, RulesViewModel rulesViewModel, MainWindow mainWindow)
    {
        ViewModel = viewModel;
        _rulesViewModel = rulesViewModel;
        _mainWindow = mainWindow;
        InitializeComponent();
        _logger = App.Current.Services.GetService<ILogger<HomePage>>();
    }

    public HomeViewModel ViewModel { get; }

    private void OnUndoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.UndoVisibilityChangeCommand.Execute(null);
        args.Handled = true;
    }

    private void OnMuteToggleClicked(object sender, RoutedEventArgs e)
    {
        // ItemsRepeater doesn't propagate DataContext to x:Bind templates - the button
        // carries the DeviceCard via Tag="{x:Bind}" instead.
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;

        var prevVolume = card.Volume;
        var prevMuted = card.IsMuted;
        card.ToggleMuteCommand.Execute(null);
        // Mute icon clicks only change IsMuted; carry the unchanged volume so Ctrl+Z
        // restores both together as one entry.
        ViewModel.RecordVolumeMuteUndo(card, prevVolume, prevMuted);
    }

    private void OnRuleChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleSummary summary }) return;

        _rulesViewModel.RequestFocusRule(summary.RuleId);
        _mainWindow.NavigateByTag("Rules");
    }

    private void OnRulesExpandToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        // ItemsRepeater doesn't propagate DataContext into x:Bind templates, so the button
        // carries the DeviceCard reference via Tag="{x:Bind}" instead.
        if (element.Tag is not DeviceCard card) return;

        var scrollViewer = FindAncestorScrollViewer(element);
        var collapsing = card.IsRulesExpanded;

        // Capture the current visible height before any state change so the animation starts
        // from where the user can see it - even if a previous tween is still mid-flight.
        var fromHeight = scrollViewer?.ActualHeight ?? 0;

        card.IsRulesExpanded = !card.IsRulesExpanded;

        if (scrollViewer is not null)
        {
            // The x:Bind on MaxHeight has just snapped to the new target. Override it back to
            // the previous height, then animate up/down to the new RulesPanelMaxHeight value.
            scrollViewer.MaxHeight = fromHeight;
            AnimateRulesExpansion(scrollViewer, fromHeight, card.RulesPanelMaxHeight);

            if (collapsing)
            {
                // Was expanded, now collapsing: snap the scroll back to the top of the list.
                scrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            }
        }
    }

    private void AnimateRulesExpansion(ScrollViewer target, double from, double to)
    {
        if (_rulesExpandStoryboards.TryGetValue(target, out var previous))
        {
            previous.Stop();
        }

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // MaxHeight affects layout, so the animation has to be marked dependent.
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "MaxHeight");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        _rulesExpandStoryboards[target] = storyboard;
        storyboard.Begin();
    }

    /// <summary>Walks up the visual tree to the first ancestor StackPanel that contains a
    /// ScrollViewer named <c>RulesScroll</c> and returns it.</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is StackPanel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is ScrollViewer sv && sv.Name == "RulesScroll")
                    {
                        return sv;
                    }
                }
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // CA1822 suppressed: XAML event hookup requires instance methods even when the body
    // doesn't touch instance state.
#pragma warning disable CA1822

    private void OnSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider { Tag: DeviceCard card } slider)
        {
            _sliderDragStart[slider] = (card.Volume, card.IsMuted);
        }
    }

    private void OnSliderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Slider { Tag: DeviceCard card } slider) return;

        FinaliseSliderInteraction(slider, card);
        card.PlayPing();
    }

    private void OnSliderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSliderNudgeKey(e.Key)) return;
        if (sender is Slider { Tag: DeviceCard card } slider &&
            !_sliderDragStart.ContainsKey(slider))
        {
            _sliderDragStart[slider] = (card.Volume, card.IsMuted);
        }
    }

    private void OnSliderKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSliderNudgeKey(e.Key)) return;
        if (sender is not Slider { Tag: DeviceCard card } slider) return;

        FinaliseSliderInteraction(slider, card);
        card.PlayPing();
    }

    private void OnSliderLostFocus(object sender, RoutedEventArgs e)
    {
        // Belt-and-suspenders: if focus moves away mid-interaction (e.g. window deactivated),
        // commit whatever change we have so the undo entry isn't lost.
        if (sender is Slider { Tag: DeviceCard card } slider)
        {
            FinaliseSliderInteraction(slider, card);
        }
    }

    private void FinaliseSliderInteraction(Slider slider, DeviceCard card)
    {
        if (!_sliderDragStart.TryGetValue(slider, out var start)) return;
        _sliderDragStart.Remove(slider);
        ViewModel.RecordVolumeMuteUndo(card, start.Volume, start.Muted);
    }

    private static bool IsSliderNudgeKey(VirtualKey key) =>
        key is VirtualKey.Left or VirtualKey.Right
            or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.PageUp or VirtualKey.PageDown
            or VirtualKey.Home or VirtualKey.End;

    private void OnLockedSliderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceCard card })
        {
            card.PlayPing();
        }
    }
#pragma warning restore CA1822
}
