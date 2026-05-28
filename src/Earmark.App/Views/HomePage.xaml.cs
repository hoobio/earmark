using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

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

        var collapsing = card.IsRulesExpanded;
        card.IsRulesExpanded = !card.IsRulesExpanded;

        if (collapsing)
        {
            // Was expanded, now collapsing: snap the scroll back to the top of the list.
            var scrollViewer = FindAncestorScrollViewer(element);
            scrollViewer?.ChangeView(null, 0, null, disableAnimation: false);
        }
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
