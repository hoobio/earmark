using System.Runtime.InteropServices.WindowsRuntime;

using Earmark.App.ViewModels;
using Earmark.Core.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI;

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

        // The page + VM are singletons, so the 20Hz peak/meter poll would otherwise run for the
        // whole app lifetime. Only run it while the page is in the visual tree: this keeps its
        // UI-thread COM reads from starving the navigate-away transition and from burning CPU
        // on other pages. Loaded/Unloaded fire on every Frame content swap.
        Loaded += (_, _) => ViewModel.ResumePeakPolling();
        Unloaded += (_, _) => ViewModel.PausePeakPolling();
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

    // A rule-locked (disabled) slider doesn't capture the pointer the way an enabled one does, so
    // a press-drag over it would otherwise bubble to the card's CanDrag and start a reorder. The
    // transparent lock overlay captures the pointer on press (mirroring the enabled slider) to keep
    // the gesture off the card; the tooltip and tap-to-ping still work.
    private void OnLockedSliderPointerPressed(object sender, PointerRoutedEventArgs e) =>
        (sender as UIElement)?.CapturePointer(e.Pointer);

    private void OnLockedSliderPointerReleased(object sender, PointerRoutedEventArgs e) =>
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);

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
    private const string DragPayloadCardPrefix = "earmark:card:";

    /// <summary>The card currently showing a reorder insertion rule. The page lights exactly
    /// one card at a time during a card drag; this tracks it so each new DragOver / Drop /
    /// DropCompleted can clear the previous one without scanning every card.</summary>
    private DeviceCard? _reorderHighlightCard;

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

    // ---- Device card reorder drag ----
    //
    // The card Border is both a drag source (reorder) and a drop target (app chips + reorder).
    // CanDrag="True" yields the drag to interactive children that capture the pointer (slider,
    // mute button, app chips, rule chips) while a grab on the card background / labels starts a
    // reorder. Payload is "earmark:card:{endpointId}"; the Drop handler disambiguates strictly
    // by prefix against the chip payload above.

    private async void OnDeviceCardDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card } element) return;
        args.Data.SetText($"{DragPayloadCardPrefix}{card.Endpoint.Id}");
        args.Data.RequestedOperation = DataPackageOperation.Move;

        // Shrink every other card so the dragged one reads as "lifted".
        ViewModel.SetReorderInProgress(true, card.Endpoint.Id);

        // The default drag bitmap is translucent: the card fill is a semi-transparent layer brush
        // meant to sit over Mica, so lifted off the backdrop it reads as see-through. Render an
        // opaque snapshot and use that as the drag visual instead.
        var deferral = args.GetDeferral();
        try
        {
            var bitmap = await RenderCardOpaqueAsync(element);
            if (bitmap is not null)
            {
                args.DragUI.SetContentFromSoftwareBitmap(bitmap);
            }
        }
        catch
        {
            // Keep the default (translucent) visual if the snapshot fails.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDeviceCardDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        // Drag finished (dropped on a card, dropped on empty space, or cancelled) - drop any
        // lingering insertion rule and un-shrink the other cards. The Drop handler also clears the
        // rule, but a cancelled drag never reaches it, so this is the catch-all.
        ClearReorderHighlight();
        ViewModel.SetReorderInProgress(false);
    }

    /// <summary>Renders the card to an opaque bitmap for use as the drag visual. The card's own
    /// fill is a translucent layer brush, so each premultiplied pixel is composited over the
    /// theme's solid background colour to make the lifted card read as solid.</summary>
    private static async Task<SoftwareBitmap?> RenderCardOpaqueAsync(FrameworkElement element)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0) return null;

        var bytes = (await rtb.GetPixelsAsync()).ToArray();   // BGRA8, premultiplied alpha
        var baseColor = element.ActualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 0xF3, 0xF3, 0xF3)           // SolidBackgroundFillColorBase (light)
            : Color.FromArgb(255, 0x20, 0x20, 0x20);          // SolidBackgroundFillColorBase (dark)

        for (var i = 0; i + 3 < bytes.Length; i += 4)
        {
            var a = bytes[i + 3];
            if (a == 255) continue;
            var inv = 255 - a;
            bytes[i + 0] = (byte)(bytes[i + 0] + (baseColor.B * inv / 255));
            bytes[i + 1] = (byte)(bytes[i + 1] + (baseColor.G * inv / 255));
            bytes[i + 2] = (byte)(bytes[i + 2] + (baseColor.R * inv / 255));
            bytes[i + 3] = 255;
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, rtb.PixelWidth, rtb.PixelHeight, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(bytes.AsBuffer());
        return bitmap;
    }

    private void SetReorderHighlight(DeviceCard card, bool insertAfter)
    {
        if (!ReferenceEquals(_reorderHighlightCard, card))
        {
            ClearReorderHighlight();
            _reorderHighlightCard = card;
        }
        card.ShowInsertBefore = !insertAfter;
        card.ShowInsertAfter = insertAfter;
    }

    private void ClearReorderHighlight()
    {
        if (_reorderHighlightCard is null) return;
        _reorderHighlightCard.ShowInsertBefore = false;
        _reorderHighlightCard.ShowInsertAfter = false;
        _reorderHighlightCard = null;
    }

    private void OnDeviceCardDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card } border) return;

        // Bail early when the drag isn't ours. Other drags (file drops onto the window, etc.)
        // shouldn't get our acceptance.
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // Read the payload once, then branch on prefix: card-reorder vs app-chip move.
        var text = TryReadText(e.DataView);
        if (text is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (text.StartsWith(DragPayloadCardPrefix, StringComparison.Ordinal))
        {
            var sourceId = text.Substring(DragPayloadCardPrefix.Length);
            // Dropping a card on itself is a no-op; no insertion rule.
            if (string.Equals(card.Endpoint.Id, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                ClearReorderHighlight();
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else
            {
                // Insert side = which half of the card the pointer is over.
                var insertAfter = e.GetPosition(border).X > border.ActualWidth / 2;
                SetReorderHighlight(card, insertAfter);
                e.AcceptedOperation = DataPackageOperation.Move;
            }
            e.Handled = true;
            return;
        }

        if (TryParseChipPayload(text, out var pid, out var sourceEndpointId))
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
        if (sender is not FrameworkElement { Tag: DeviceCard card } border) return;

        var text = TryReadText(e.DataView);
        if (text is null) return;

        if (text.StartsWith(DragPayloadCardPrefix, StringComparison.Ordinal))
        {
            ClearReorderHighlight();
            var sourceId = text.Substring(DragPayloadCardPrefix.Length);
            if (string.Equals(card.Endpoint.Id, sourceId, StringComparison.OrdinalIgnoreCase)) return;

            var insertAfter = e.GetPosition(border).X > border.ActualWidth / 2;
            _logger?.LogInformation(
                "Reorder: {Source} -> {Target} (after={After})",
                sourceId, card.Endpoint.Id, insertAfter);
            ViewModel.ReorderDevice(sourceId, card, insertAfter);
            e.Handled = true;
            return;
        }

        if (card.IsCapture) return;
        if (!TryParseChipPayload(text, out var pid, out var sourceEndpointId)) return;
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

    /// <summary>Reads the in-process drag payload text once. GetTextAsync is async; the
    /// DragOver/Drop handlers can't await without losing the synchronous accept decision, so we
    /// block on it - the DataPackage source is in-process and already resolved. Returns null when
    /// there's no text or it can't be read.</summary>
    private static string? TryReadText(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.Text)) return null;
        try
        {
            return view.GetTextAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseChipPayload(string text, out uint pid, out string sourceEndpointId)
    {
        pid = 0;
        sourceEndpointId = string.Empty;
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
