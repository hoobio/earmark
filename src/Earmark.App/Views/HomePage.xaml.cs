using Earmark.App.ViewModels;
using Earmark.Core.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel.DataTransfer;
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
            // Was expanded, now collapsing: snap the scroll back to the top of the list so
            // the next expand starts from the first chip.
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

    // ---- App chip drag / drop ----
    //
    // In-process drag of an AppChip onto a render DeviceCard rebinds the session's per-app
    // default endpoint. The DataPackage Text carries an "earmark:chip:{pid}:{sourceEndpointId}"
    // sentinel; the Drop handler parses it back into a chip + target card and asks the VM
    // to apply the override via IAudioPolicyService.
    //
    // Cursor feedback is OS-native via DataPackageOperation.None - WinUI draws the slashed
    // circle the user expects when DragOver decides the drop isn't valid (capture endpoint
    // target, or dropping back on the source card). No custom cursor work needed.

    private const string DragPayloadPrefix = "earmark:chip:";

    private void OnAppChipDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: AppChip chip }) return;
        if (!chip.CanDrag)
        {
            args.Cancel = true;
            return;
        }

        // Payload is parsed in OnDeviceCardDrop. Keep it small; the AppChip itself doesn't
        // have to round-trip - the page resolves PID + source endpoint back to the live chip
        // via the HomeViewModel's card list, which is the source of truth.
        var payload = $"{DragPayloadPrefix}{chip.ProcessId}|{chip.SourceEndpointId}";
        args.Data.SetText(payload);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnAppChipDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        // Nothing to clean up - the payload is value-typed text. Hook exists so future
        // affordances (e.g. flash the source card on a successful move) have a place to land.
    }

    private void OnDeviceCardDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;

        // Bail early when the drag isn't ours. Other drags (file drops onto the window, etc.)
        // shouldn't get our acceptance.
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (TryReadPayload(e.DataView, out var pid, out var sourceEndpointId))
        {
            _ = pid;
            // Capture endpoint -> cursor shows slashed circle. Same goes for dropping on the
            // source card (no-op). Anything else accepts as Move.
            if (card.IsCapture ||
                string.Equals(card.Endpoint.Id, sourceEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.Move;
            }
            e.Handled = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void OnDeviceCardDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;
        if (card.IsCapture) return;
        if (!TryReadPayload(e.DataView, out var pid, out var sourceEndpointId)) return;
        if (string.Equals(card.Endpoint.Id, sourceEndpointId, StringComparison.OrdinalIgnoreCase)) return;

        var chip = FindChipByPid(pid);
        if (chip is null)
        {
            _logger?.LogInformation("Drop: chip with pid={Pid} no longer present, ignoring", pid);
            return;
        }

        _logger?.LogInformation(
            "Drop: pid={Pid} {Source} -> {Target}",
            pid, sourceEndpointId, card.Endpoint.Id);
        ViewModel.MoveSessionToEndpoint(chip, card.Endpoint);
        e.Handled = true;
    }

    private AppChip? FindChipByPid(uint pid)
    {
        foreach (var card in ViewModel.Devices)
        {
            foreach (var chip in card.Apps)
            {
                if (chip.ProcessId == pid) return chip;
            }
        }
        return null;
    }

    private static bool TryReadPayload(DataPackageView view, out uint pid, out string sourceEndpointId)
    {
        pid = 0;
        sourceEndpointId = string.Empty;
        if (!view.Contains(StandardDataFormats.Text)) return false;

        // GetTextAsync is async; the DragOver/Drop handlers can't await without losing the
        // synchronous accept decision. Block on the async via GetResults - the source of
        // this DataPackage is in-process so it's already resolved.
        string text;
        try
        {
            text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
        if (string.IsNullOrEmpty(text) || !text.StartsWith(DragPayloadPrefix, StringComparison.Ordinal)) return false;

        var body = text.Substring(DragPayloadPrefix.Length);
        var sep = body.IndexOf('|');
        if (sep <= 0 || sep == body.Length - 1) return false;
        if (!uint.TryParse(body.AsSpan(0, sep), System.Globalization.CultureInfo.InvariantCulture, out pid)) return false;
        sourceEndpointId = body.Substring(sep + 1);
        return true;
    }
#pragma warning restore CA1822
}
