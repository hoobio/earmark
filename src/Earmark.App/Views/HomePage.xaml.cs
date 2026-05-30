using System.Runtime.InteropServices.WindowsRuntime;

using Earmark.App.Controls;
using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    private BlockWrapLayout? Layout => DevicesRepeater.Layout as BlockWrapLayout;

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

#pragma warning restore CA1822

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

        SetDragInProgress(true);
    }

    private void OnAppChipDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        SetDragInProgress(false);
    }

    /// <summary>Reveals every group container's dotted outline while a drag is in flight, so groups
    /// read as transparent at rest and show their bounds only while dragging.</summary>
    private void SetDragInProgress(bool active)
    {
        foreach (var block in ViewModel.Blocks)
        {
            if (block is DeviceGroupCard group) group.ShowOutline = active;
        }
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

        var text = TryReadText(e.DataView);
        if (text is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
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
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;

        var text = TryReadText(e.DataView);
        if (text is null) return;

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
        foreach (var card in ViewModel.VisibleCards)
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

    /// <summary>Renders the card to an opaque bitmap for use as the drag visual. The card's own
    /// fill is a translucent layer brush, so each premultiplied pixel is composited over the
    /// theme's solid background colour to make the lifted card read as solid. The card's rounded
    /// corners are then re-applied as an alpha mask: compositing over an opaque base fills the
    /// transparent corner cut-outs with solid colour and squares the card off, so we punch them
    /// back out.</summary>
    private static async Task<SoftwareBitmap?> RenderCardOpaqueAsync(FrameworkElement element)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var w = rtb.PixelWidth;
        var h = rtb.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        var bytes = (await rtb.GetPixelsAsync()).ToArray();   // BGRA8, premultiplied alpha
        var baseColor = element.ActualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 0xF3, 0xF3, 0xF3)           // SolidBackgroundFillColorBase (light)
            : Color.FromArgb(255, 0x20, 0x20, 0x20);          // SolidBackgroundFillColorBase (dark)

        // Corner radius in physical pixels: the card's DIP radius scaled by the render's
        // rasterization scale (rendered pixel width / layout width).
        var radiusDip = (element as Border)?.CornerRadius.TopLeft ?? 8.0;
        var scale = element.ActualWidth > 0 ? w / element.ActualWidth : 1.0;
        var radius = radiusDip * scale;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var coverage = RoundedRectCoverage(x + 0.5, y + 0.5, w, h, radius);
                var a = bytes[i + 3];
                if (a == 255 && coverage >= 1.0) continue;   // opaque interior - leave it

                // Composite the (premultiplied) source over the opaque base, then re-premultiply
                // by the corner coverage so the rounded cut-outs stay transparent.
                var inv = 255 - a;
                var b = bytes[i + 0] + baseColor.B * inv / 255.0;
                var g = bytes[i + 1] + baseColor.G * inv / 255.0;
                var r = bytes[i + 2] + baseColor.R * inv / 255.0;
                bytes[i + 0] = (byte)(b * coverage);
                bytes[i + 1] = (byte)(g * coverage);
                bytes[i + 2] = (byte)(r * coverage);
                bytes[i + 3] = (byte)(255 * coverage);
            }
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(bytes.AsBuffer());
        return bitmap;
    }

    /// <summary>Anti-aliased coverage [0,1] of a pixel centre against a rounded rectangle: 1 over
    /// the straight edges and interior, a soft ramp across each corner arc, 0 outside it.</summary>
    private static double RoundedRectCoverage(double px, double py, double w, double h, double r)
    {
        if (r <= 0) return 1.0;
        // Pick the nearest corner-arc centre; bail to full coverage on the straight-edge bands.
        double cx;
        if (px < r) cx = r; else if (px > w - r) cx = w - r; else return 1.0;
        double cy;
        if (py < r) cy = r; else if (py > h - r) cy = h - r; else return 1.0;
        var dx = px - cx;
        var dy = py - cy;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp(r - dist + 0.5, 0.0, 1.0);
    }
}
