using System.Runtime.InteropServices.WindowsRuntime;

using Earmark.App.Controls;
using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

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
        Unloaded += (_, _) =>
        {
            ViewModel.PausePeakPolling();
            // Pointer-exit may not fire on navigate-away; clear so the "..." doesn't linger.
            _pointerOverDevices = false;
            UpdateOverflowVisibility();
        };

        // The floating "..." auto-hides: shown only while the pointer is over the page, it's
        // keyboard-focused, or its menu is open. Pointer-over is tracked on the page root.
        DevicesRootGrid.PointerEntered += (_, _) => { _pointerOverDevices = true; UpdateOverflowVisibility(); };
        DevicesRootGrid.PointerExited += (_, _) => { _pointerOverDevices = false; UpdateOverflowVisibility(); };
        UpdateOverflowVisibility();
    }

    // ---- Floating overflow ("...") auto-hide ----
    private bool _pointerOverDevices;
    private bool _overflowFocused;
    private bool _overflowFlyoutOpen;

    private void UpdateOverflowVisibility() =>
        OverflowButton.Opacity = _pointerOverDevices || _overflowFocused || _overflowFlyoutOpen ? 1 : 0;

    private void OnOverflowFocusChanged(object sender, RoutedEventArgs e)
    {
        _overflowFocused = OverflowButton.FocusState != FocusState.Unfocused;
        UpdateOverflowVisibility();
    }

    private void OnOverflowFlyoutOpening(object? sender, object e)
    {
        _overflowFlyoutOpen = true;
        UpdateOverflowVisibility();
    }

    private void OnOverflowFlyoutClosed(object? sender, object e)
    {
        _overflowFlyoutOpen = false;
        UpdateOverflowVisibility();
    }

    public HomeViewModel ViewModel { get; }

    private BlockWrapLayout? Layout => DevicesRepeater.Layout as BlockWrapLayout;

    // ---- Block reorder + move-whole-group ----
    //
    // The top level is a list of blocks (a lone DeviceCard or a DeviceGroupCard section). A reorder
    // drag lifts one block and the others slide to open a gap; the dropped block lands at the gap.
    // A group is one block, so a reorder can never split it. Drag sources: a lone card's Border, and
    // a group's title band (header handle). Payloads: "earmark:card:{endpointId}" /
    // "earmark:group:{groupId}". The drop is committed at the container (OnBlocksDrop) using the
    // layout's frozen no-gap geometry, so the insert point is stable while blocks slide.

    private const string DragPayloadCardPrefix = "earmark:card:";
    private const string DragPayloadGroupPrefix = "earmark:group:";

    /// <summary>The card being dragged (a lone card or a group member), or null. App-chip drags leave
    /// this null (they're handled per card).</summary>
    private DeviceCard? _draggedCard;

    /// <summary>The group the dragged card belongs to when it's a member; null for a lone card.</summary>
    private DeviceGroupCard? _draggedCardGroup;

    /// <summary>The group being dragged by its header handle, or null.</summary>
    private DeviceGroupCard? _draggedGroup;

    /// <summary>The lone card currently highlighted as a create-group target (accent dotted outline).</summary>
    private DeviceCard? _createTarget;

    /// <summary>The group currently highlighted as a join target (accent outline).</summary>
    private DeviceGroupCard? _joinTarget;


    /// <summary>The group's inner layout currently showing a member make-space gap (within-group
    /// reorder) or a phantom join slot, or null.</summary>
    private WrapByRowLayout? _activeInnerLayout;

    /// <summary>The inner member repeater currently carrying the implicit slide animation, or null.</summary>
    private ItemsRepeater? _animatedInner;

    /// <summary>The group whose title is currently being edited, or null. Used to commit the edit when
    /// the user clicks away onto a non-focusable area (which wouldn't otherwise blur the text box).</summary>
    private DeviceGroupCard? _editingGroup;

    private async void OnDeviceCardDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (ViewModel.LockLayout) { args.Cancel = true; return; }
        if (sender is not FrameworkElement { Tag: DeviceCard card } element) return;

        _draggedCard = card;
        _draggedCardGroup = card.IsGroupMember ? FindGroupOf(card) : null;
        args.Data.SetText($"{DragPayloadCardPrefix}{card.Endpoint.Id}");
        args.Data.RequestedOperation = DataPackageOperation.Move;

        SetDragInProgress(true);

        // Opaque drag bitmap (the card fill is translucent, so lifted off the backdrop it reads as
        // see-through). Render before hiding the source.
        var deferral = args.GetDeferral();
        try
        {
            var bitmap = await RenderCardOpaqueAsync(element);
            if (bitmap is not null) args.DragUI.SetContentFromSoftwareBitmap(bitmap);
        }
        catch { /* keep the default visual if the snapshot fails */ }
        finally { deferral.Complete(); }

        card.IsBeingDragged = true;
    }

    private void OnDeviceCardDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_draggedCard is not null) _draggedCard.IsBeingDragged = false;
        EndDrag();
    }

    private async void OnGroupHeaderDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (ViewModel.LockLayout) { args.Cancel = true; return; }
        if (sender is not FrameworkElement { Tag: DeviceGroupCard group }) return;

        _draggedGroup = group;
        args.Data.SetText($"{DragPayloadGroupPrefix}{group.Id}");
        args.Data.RequestedOperation = DataPackageOperation.Move;

        SetDragInProgress(true);   // reveals the dotted outline + drag padding on every group

        // Drag visual: a snapshot of the group BOX (cards + title + its dotted outline), bounded to
        // the members' extent - not the full-width section. The box is the block element's first
        // child (the left-aligned, member-width Grid). Render after the outline + padding apply, and
        // before hiding the source.
        var index = ViewModel.Blocks.IndexOf(group);
        var element = index >= 0 ? DevicesRepeater.TryGetElement(index) : null;
        var box = (element as Panel)?.Children.FirstOrDefault() as FrameworkElement ?? element as FrameworkElement;
        if (box is not null)
        {
            var deferral = args.GetDeferral();
            try
            {
                box.UpdateLayout();
                var bitmap = await RenderCardOpaqueAsync(box);
                if (bitmap is not null) args.DragUI.SetContentFromSoftwareBitmap(bitmap);
            }
            catch { /* keep the default visual if the snapshot fails */ }
            finally { deferral.Complete(); }
        }

        group.IsBeingDragged = true;
    }

    private void OnGroupHeaderDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_draggedGroup is not null) _draggedGroup.IsBeingDragged = false;
        EndDrag();
    }

    // ---- Group title editing + context menu ----

    private void OnGroupTitleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceGroupCard group } header) return;
        group.IsEditingTitle = true;
        _editingGroup = group;
        FocusTitleEditor(header);
        e.Handled = true;
    }

    private void OnRenameGroupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || GroupFromTag(fe.Tag) is not { } group) return;
        group.IsEditingTitle = true;
        _editingGroup = group;
        // The flyout item isn't in the header's tree; focus the editor via the realised block element.
        var idx = ViewModel.Blocks.IndexOf(group);
        if (idx >= 0 && DevicesRepeater.TryGetElement(idx) is FrameworkElement blockEl)
        {
            FocusTitleEditor(blockEl);
        }
    }

    private void OnUngroupAllClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && GroupFromTag(fe.Tag) is { } group)
        {
            ViewModel.UngroupAll(group.Id);
        }
    }

    /// <summary>Resolves the group a flyout item targets: directly when invoked from a group header
    /// (tag = the group), or the parent group when invoked from a member card (tag = the card).</summary>
    private DeviceGroupCard? GroupFromTag(object? tag) => tag switch
    {
        DeviceGroupCard group => group,
        DeviceCard card => FindGroupOf(card),
        _ => null,
    };

    private void OnUngroupDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceCard card })
        {
            ViewModel.UngroupDevice(card.Endpoint.Id);
        }
    }

    private void OnCustomiseClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceCard card }) return;
        // Defer so the context MenuFlyout finishes dismissing before the dialog opens.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = BuildCustomiseDialog(card);
                dialog.XamlRoot = XamlRoot;
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Customise: dialog threw");
            }
        });
    }

    private const double CustomiseWidth = 280;

    private static ContentDialog BuildCustomiseDialog(DeviceCard card)
    {
        // Snapshot the saved state. The dialog edits a PENDING copy and only writes it back to the
        // card on Save - the card and its tile are never touched mid-edit, so Cancel is a no-op and
        // the live device tile doesn't flicker while you experiment. The preview mirrors the pending
        // state using the card's auto-derived glyph/accent for the "Auto" fallbacks.
        var origGlyph = card.CurrentGlyphOverride;
        var origAccent = card.CurrentAccent;
        var origNone = card.IsAccentNone;
        var origVolumeHidden = card.IsVolumeControlsHiddenByUser;

        var pendingGlyph = origGlyph;
        var pendingAccent = origAccent;
        var pendingNone = origNone;
        var pendingVolumeHidden = origVolumeHidden;

        var accentBrushRes = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var strokeBrushRes = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        var subtleBrushRes = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        var accentTextRes = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

        var root = new StackPanel { Spacing = 16, Width = CustomiseWidth };

        // ---- Live preview header (mirrors the card: tile + name / subtitle / flow) ----
        var previewTile = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(8) };
        var previewIcon = new FontIcon { FontSize = 24 };
        previewTile.Child = previewIcon;

        var info = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = card.DeviceNameOnly,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (card.HasDeviceIdSubtext)
        {
            info.Children.Add(new TextBlock
            {
                Text = card.DeviceIdSubtext,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        info.Children.Add(new TextBlock
        {
            Text = card.FlowLabel,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(previewTile, 0);
        Grid.SetColumn(info, 1);
        header.Children.Add(previewTile);
        header.Children.Add(info);
        root.Children.Add(header);

        root.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        // ---- Glyph section ----
        var glyphHost = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Width = CustomiseWidth };
        var glyphButtons = new List<Button>();
        Controls.ColourSwatchPicker colourPicker = null!;
        ContentDialog dialog = null!;
        Button saveBtn = null!;
        Button resetBtn = null!;
        var suppressColour = false;

        bool IsDirty() =>
            pendingGlyph != origGlyph
            || pendingAccent != origAccent
            || pendingNone != origNone
            || pendingVolumeHidden != origVolumeHidden;

        // "Reset to default" only does something when the pending state isn't already fully default.
        bool PendingIsDefault() =>
            pendingGlyph is null && pendingAccent is null && !pendingNone && !pendingVolumeHidden;

        void RefreshAll()
        {
            // Preview mirrors the PENDING choice, falling back to the card's auto-derived glyph /
            // accent for unset axes - without mutating the card.
            var effGlyph = pendingGlyph ?? card.AutoGlyph;
            var effAccent = pendingNone ? (Color?)null : pendingAccent ?? card.AutoAccent;
            previewTile.Background = effAccent is { } c ? new SolidColorBrush(c) : subtleBrushRes;
            previewIcon.Glyph = effGlyph;
            previewIcon.Foreground = effAccent is { } ec ? DeviceCard.ContrastBrushFor(ec) : accentTextRes;

            // Outline the curated glyph matching the effective (pending) glyph.
            foreach (var btn in glyphButtons)
            {
                var selected = (string)btn.Tag == effGlyph;
                btn.BorderThickness = new Thickness(selected ? 2 : 1);
                btn.BorderBrush = selected ? accentBrushRes : strokeBrushRes;
            }

            // Save is a custom accent button: enabled (renders blue) only while the pending state
            // differs from the saved one, disabled (renders grey) otherwise. Custom rather than the
            // ContentDialog primary because that button's accent doesn't re-apply when toggled at
            // runtime. Reset is disabled when there's nothing to reset, so it never reads as a no-op.
            if (saveBtn is not null) saveBtn.IsEnabled = IsDirty();
            if (resetBtn is not null) resetBtn.IsEnabled = !PendingIsDefault();
        }

        root.Children.Add(SectionCaption("Glyph"));
        foreach (var (label, glyph) in DeviceGlyphMapper.CuratedGlyphs)
        {
            var btn = new Button
            {
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                Tag = glyph,
                Content = new FontIcon { Glyph = glyph, FontSize = 20 },
            };
            ToolTipService.SetToolTip(btn, label);
            btn.Click += (_, _) => { pendingGlyph = glyph; RefreshAll(); };
            glyphHost.Children.Add(btn);
            glyphButtons.Add(btn);
        }
        // Trailing "more glyphs" tile opens the full Fluent browser in a flyout.
        var moreGlyphTile = new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 18 }, // More (ellipsis)
            Flyout = BuildGlyphBrowserFlyout(g => { pendingGlyph = g; RefreshAll(); }),
        };
        ToolTipService.SetToolTip(moreGlyphTile, "More glyphs…");
        glyphHost.Children.Add(moreGlyphTile);
        root.Children.Add(glyphHost);

        // ---- Accent colour section ----
        root.Children.Add(SectionCaption("Accent colour"));
        colourPicker = new Controls.ColourSwatchPicker { ShowNone = true };
        void SeedColour()
        {
            // Reflect the pending state. For an unset (Auto) colour, show the card's auto accent so
            // its swatch reads as selected, without making it an explicit override.
            suppressColour = true;
            colourPicker.IsNoneSelected = pendingNone;
            colourPicker.SelectedColour = pendingNone ? null : pendingAccent ?? card.AutoAccent;
            suppressColour = false;
        }
        SeedColour();
        colourPicker.RegisterPropertyChangedCallback(
            Controls.ColourSwatchPicker.SelectedColourProperty,
            (_, _) =>
            {
                if (suppressColour || colourPicker.SelectedColour is not Color c) return;
                pendingNone = false;
                pendingAccent = c;
                RefreshAll();
            });
        colourPicker.NoneRequested += (_, _) =>
        {
            pendingNone = true;
            pendingAccent = null;
            RefreshAll();
        };
        root.Children.Add(colourPicker);

        // ---- Volume controls toggle (moved off the context menu) ----
        root.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        var volumeCheck = new CheckBox
        {
            // Just the slider - the icon tile stays a working mute toggle regardless of this.
            Content = "Show volume slider",
            IsChecked = !pendingVolumeHidden,
        };
        ToolTipService.SetToolTip(volumeCheck,
            "Hide the slider for devices whose volume Windows can set but that ignore it (e.g. a USB DAC/amp with its own knob). The icon stays a mute toggle.");
        volumeCheck.Checked += (_, _) => { pendingVolumeHidden = false; RefreshAll(); };
        volumeCheck.Unchecked += (_, _) => { pendingVolumeHidden = true; RefreshAll(); };
        root.Children.Add(volumeCheck);

        // ---- Custom button row (pinned below the scroll area) ----
        // Save is a real AccentButton so its blue/grey state tracks IsEnabled reliably (the
        // ContentDialog primary button's accent doesn't re-apply when enabled at runtime).
        saveBtn = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        resetBtn = new Button { Content = "Reset to default" };
        var cancelBtn = new Button { Content = "Cancel" };

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelBtn, saveBtn },
        };
        var buttonBar = new Grid { Padding = new Thickness(0, 16, 0, 0) };
        buttonBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(resetBtn, 0);
        Grid.SetColumn(rightButtons, 1);
        resetBtn.HorizontalAlignment = HorizontalAlignment.Left;
        buttonBar.Children.Add(resetBtn);
        buttonBar.Children.Add(rightButtons);

        // Content: scrollable body (sized to content, capped so it scrolls only when too tall)
        // above a pinned button row.
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroller = new ScrollViewer
        {
            Content = root,
            MaxHeight = 640, // high enough that normal content doesn't scroll; only short windows do
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 0);
        Grid.SetRow(buttonBar, 1);
        outer.Children.Add(scroller);
        outer.Children.Add(buttonBar);

        dialog = new ContentDialog { Title = "Customise", Content = outer };
        RefreshAll(); // sync the buttons' enabled state now that they exist

        saveBtn.Click += (_, _) =>
        {
            // Commit every axis. Set the volume flag first so SetUserCustomisation's persist
            // (UpdateDeviceConfig) writes it alongside the glyph / accent.
            card.IsVolumeControlsHiddenByUser = pendingVolumeHidden;
            card.SetUserCustomisation(pendingGlyph, pendingAccent, pendingNone);
            dialog.Hide();
        };
        cancelBtn.Click += (_, _) => dialog.Hide(); // discard: the card was never mutated
        resetBtn.Click += (_, _) =>
        {
            pendingGlyph = null;
            pendingAccent = null;
            pendingNone = false;
            pendingVolumeHidden = false;
            volumeCheck.IsChecked = true;
            SeedColour();
            RefreshAll();
        };
        return dialog;
    }

    private static TextBlock SectionCaption(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    /// <summary>Flyout: a search box (filter by name or hex) over a scrollable grid of every Segoe
    /// Fluent glyph, single-select. Calls <paramref name="onPick"/> with the chosen glyph. Each cell
    /// tooltips its name.</summary>
    private static Flyout BuildGlyphBrowserFlyout(Action<string> onPick)
    {
        var all = DeviceGlyphMapper.AllFluentGlyphs;
        // The GridView's built-in ScrollViewer virtualizes the 1500+ items - do NOT wrap it in an
        // outer ScrollViewer (that gives it infinite height and realizes every item, which lags).
        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            Width = 320,
            Height = 320,
            ItemsSource = new List<DeviceGlyphMapper.GlyphEntry>(all),
            ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
                + "<FontIcon Glyph=\"{Binding Glyph}\" FontSize=\"20\" Width=\"32\" Height=\"32\" "
                + "ToolTipService.ToolTip=\"{Binding Name}\" /></DataTemplate>"),
        };
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is DeviceGlyphMapper.GlyphEntry e)
            {
                onPick(e.Glyph);
            }
        };

        var search = new TextBox { PlaceholderText = "Search name or hex (e.g. HardDrive)" };
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            grid.ItemsSource = string.IsNullOrEmpty(q)
                ? new List<DeviceGlyphMapper.GlyphEntry>(all)
                : all.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || g.Hex.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        var panel = new StackPanel { Spacing = 8, Width = 320 };
        panel.Children.Add(search);
        panel.Children.Add(grid);
        return new Flyout { Content = panel };
    }

    private void OnGroupTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        // Move focus off the TextBox so the two-way binding commits (via LostFocus), as if clicked away.
        DevicesRepeater.Focus(FocusState.Programmatic);
    }

    private void OnGroupTitleEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceGroupCard group })
        {
            group.IsEditingTitle = false;   // the two-way binding already committed the title on focus loss
            if (ReferenceEquals(_editingGroup, group)) _editingGroup = null;
        }
    }

    /// <summary>Commits an in-progress title edit when the user clicks anywhere outside the editor.
    /// Clicking a non-focusable area (empty space, a card body) wouldn't otherwise blur the text box,
    /// so move focus off it - which fires its LostFocus, committing the rename and leaving edit mode.</summary>
    private void OnContentTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editingGroup is null) return;
        if (e.OriginalSource is DependencyObject node && IsWithinTextBox(node)) return;   // tapped the editor itself
        DevicesRepeater.Focus(FocusState.Programmatic);
    }

    private static bool IsWithinTextBox(DependencyObject node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox) return true;
        }
        return false;
    }

    /// <summary>Focuses + selects the group's title text box once it becomes visible.</summary>
    private void FocusTitleEditor(FrameworkElement root)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindDescendant<TextBox>(root) is { } box)
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }
        });
    }

    /// <summary>Shared teardown for any reorder / reparent drag (committed or cancelled): drop the gap,
    /// clear highlights + outlines, the inner-group slide animation, and reset the dragged state. The
    /// block-level slide stays attached (it's always on), so blocks keep gliding after a drag.</summary>
    private void EndDrag()
    {
        _draggedCard = null;
        _draggedCardGroup = null;
        _draggedGroup = null;
        ClearHighlights();
        ClearActiveInnerGap();
        ClearInnerAnimations();
        Layout?.ClearReorderState();
        SetDragInProgress(false);
    }

    private void OnBlocksDragOver(object sender, DragEventArgs e)
    {
        if (Layout is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var point = e.GetPosition(DevicesRepeater);

        // Whole-group reorder (dragging the header).
        if (_draggedGroup is not null)
        {
            ClearHighlights();
            SetReorderGap(_draggedGroup, point);
            SetDragCaption(e, "Move group");
            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
            return;
        }

        if (_draggedCard is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (_draggedCardGroup is not null)
        {
            // Member drag: inside its group box -> reorder within (members bump to show the gap);
            // over another group -> move into it; anywhere else -> leave (become a lone card).
            var box = Layout.GetContentRect(ViewModel.Blocks.IndexOf(_draggedCardGroup));
            ClearHighlights();
            Layout.ClearReorderState();   // member moves don't open a block-level gap

            if (box.Contains(point)
                && InnerRepeaterOf(_draggedCardGroup) is { Layout: WrapByRowLayout innerLayout } inner)
            {
                EnsureInnerAnimations(inner);
                var draggedIdx = _draggedCardGroup.Members.IndexOf(_draggedCard);
                var raw = InnerInsertionIndex(inner, innerLayout, point);
                innerLayout.SetReorderState(draggedIdx, raw > draggedIdx ? raw - 1 : raw);
                SetActiveInnerGap(innerLayout);
                SetDragCaption(e, "Move within group");
            }
            else if (TryResolveOtherGroupJoin(point, out var otherGroup, out _))
            {
                SetJoinTarget(otherGroup);
                // Open a phantom slot in the target group so its members bump to preview the landing.
                if (InnerRepeaterOf(otherGroup) is { Layout: WrapByRowLayout joinLayout } joinInner)
                {
                    EnsureInnerAnimations(joinInner);
                    joinLayout.SetPhantomGap(InnerInsertionIndex(joinInner, joinLayout, point));
                    SetActiveInnerGap(joinLayout);
                }
                SetDragCaption(e, "Move to group");
            }
            else
            {
                ClearActiveInnerGap();   // left the box - close the in-group gap
                SetDragCaption(e, "Remove from group");
            }

            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
            return;
        }

        // Lone card: create (onto a card's centre) / join (onto a group) / reorder (elsewhere).
        var targetIdx = Layout.GetBlockIndexAt(point);
        var target = targetIdx >= 0 && targetIdx < ViewModel.Blocks.Count ? ViewModel.Blocks[targetIdx] : null;

        if (target is DeviceGroupCard joinGroup && ResolveGroupIntent(targetIdx, point) == GroupDropIntent.Join)
        {
            Layout.ClearReorderState();
            ClearCreateTarget();
            SetJoinTarget(joinGroup);
            // Open a phantom slot in the group so its members bump to preview where the card lands.
            if (InnerRepeaterOf(joinGroup) is { Layout: WrapByRowLayout joinLayout } joinInner)
            {
                EnsureInnerAnimations(joinInner);
                joinLayout.SetPhantomGap(InnerInsertionIndex(joinInner, joinLayout, point));
                SetActiveInnerGap(joinLayout);
            }
            SetDragCaption(e, "Add to group");
        }
        else if (target is DeviceGroupCard)
        {
            // Top / bottom strip of a group section = insert the card before / after the group (a
            // block reorder). This is the only way to drop above a first-in-row group.
            ClearHighlights();
            ClearActiveInnerGap();
            var src = ViewModel.Blocks.IndexOf(_draggedCard);
            var insertIdx = ResolveGroupIntent(targetIdx, point) == GroupDropIntent.Before ? targetIdx : targetIdx + 1;
            if (src >= 0) Layout.SetReorderState(src, ToCompactIndex(insertIdx, src));
            SetDragCaption(e, "Move");
        }
        else if (target is DeviceCard targetCard
                 && !ReferenceEquals(targetCard, _draggedCard)
                 && IsCentreZone(point, Layout.GetContentRect(targetIdx)))
        {
            Layout.ClearReorderState();
            ClearActiveInnerGap();
            ClearJoinTarget();
            SetCreateTarget(targetCard);
            SetDragCaption(e, "Group");
        }
        else
        {
            ClearHighlights();
            ClearActiveInnerGap();
            SetReorderGap(_draggedCard, point);
            SetDragCaption(e, "Move");
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    private async void OnBlocksDrop(object sender, DragEventArgs e)
    {
        if (Layout is null) return;
        var point = e.GetPosition(DevicesRepeater);
        e.Handled = true;

        // Whole-group reorder.
        if (_draggedGroup is not null)
        {
            var groupSource = ViewModel.Blocks.IndexOf(_draggedGroup);
            Layout.ClearReorderState();
            if (groupSource >= 0)
            {
                _logger?.LogInformation("Group reorder: {Group}", _draggedGroup.Id);
                ViewModel.ReorderBlock(_draggedGroup.Id, ToCompactIndex(Layout.GetInsertionIndex(point), groupSource));
            }
            return;
        }

        if (_draggedCard is null) return;
        var sourceId = _draggedCard.Endpoint.Id;

        // Member drag: leave the group (with disband confirm) or reorder within it.
        if (_draggedCardGroup is not null)
        {
            var group = _draggedCardGroup;
            var box = Layout.GetContentRect(ViewModel.Blocks.IndexOf(group));
            Layout.ClearReorderState();
            ClearHighlights();

            if (box.Contains(point))
            {
                var anchor = MemberAnchorBefore(group, point, sourceId);
                _logger?.LogInformation("Reorder within group {Group}: {Member}", group.Id, sourceId);
                ViewModel.ReorderWithinGroup(sourceId, anchor);
            }
            else if (TryResolveOtherGroupJoin(point, out var otherGroup, out _))
            {
                // Resolve the landing anchor before any await (the confirm dialog tears down the gap).
                var anchor = MemberAnchorBefore(otherGroup, point, draggedMemberId: null);
                if (ViewModel.GroupMemberCount(group.Id) <= 2 && !await ConfirmDisbandAsync())
                {
                    return;   // cancelled - the member stays in its group
                }
                _logger?.LogInformation("Move {Member} from group {From} to {To}", sourceId, group.Id, otherGroup.Id);
                ViewModel.MoveToGroup(sourceId, otherGroup.Id, anchor);
            }
            else
            {
                var anchorId = InsertionAnchorBlockId(point);
                if (ViewModel.GroupMemberCount(group.Id) <= 2 && !await ConfirmDisbandAsync())
                {
                    return;   // cancelled - the member stays in the group
                }
                _logger?.LogInformation("Leave group {Group}: {Member}", group.Id, sourceId);
                ViewModel.RemoveFromGroup(sourceId, anchorId);
            }
            return;
        }

        // Lone card: create / join / reorder.
        var targetIdx = Layout.GetBlockIndexAt(point);
        var target = targetIdx >= 0 && targetIdx < ViewModel.Blocks.Count ? ViewModel.Blocks[targetIdx] : null;
        Layout.ClearReorderState();
        ClearHighlights();

        if (target is DeviceGroupCard joinGroup)
        {
            var intent = ResolveGroupIntent(targetIdx, point);
            if (intent == GroupDropIntent.Join)
            {
                var anchor = MemberAnchorBefore(joinGroup, point, draggedMemberId: null);
                _logger?.LogInformation("Join group {Group}: {Source}", joinGroup.Id, sourceId);
                ViewModel.AddToGroup(sourceId, joinGroup.Id, anchor);
            }
            else
            {
                // Top / bottom strip: reorder the card before / after the group block.
                var src = ViewModel.Blocks.IndexOf(_draggedCard);
                var insertIdx = intent == GroupDropIntent.Before ? targetIdx : targetIdx + 1;
                if (src >= 0)
                {
                    _logger?.LogInformation("Reorder around group {Group}: {Source} ({Intent})", joinGroup.Id, sourceId, intent);
                    ViewModel.ReorderBlock(sourceId, ToCompactIndex(insertIdx, src));
                }
            }
            return;
        }
        if (target is DeviceCard targetCard
            && !ReferenceEquals(targetCard, _draggedCard)
            && IsCentreZone(point, Layout.GetContentRect(targetIdx)))
        {
            _logger?.LogInformation("Create group: {Source} + {Target}", sourceId, targetCard.Endpoint.Id);
            ViewModel.CreateGroup(sourceId, targetCard.Endpoint.Id);
            return;
        }

        var cardSource = ViewModel.Blocks.IndexOf(_draggedCard);
        if (cardSource >= 0)
        {
            _logger?.LogInformation("Block reorder: {Source}", sourceId);
            ViewModel.ReorderBlock(sourceId, ToCompactIndex(Layout.GetInsertionIndex(point), cardSource));
        }
    }

    /// <summary>Opens the block-level make-space gap for <paramref name="block"/> at the pointer.</summary>
    private void SetReorderGap(object block, Point point)
    {
        var source = ViewModel.Blocks.IndexOf(block);
        if (source < 0) { Layout?.ClearReorderState(); return; }
        Layout?.SetReorderState(source, ToCompactIndex(Layout.GetInsertionIndex(point), source));
    }

    /// <summary>The block id to insert before for a member leaving its group (null = at the end).</summary>
    private string? InsertionAnchorBlockId(Point point)
    {
        var raw = Layout!.GetInsertionIndex(point);
        return raw >= 0 && raw < ViewModel.Blocks.Count ? PageBlockId(ViewModel.Blocks[raw]) : null;
    }

    /// <summary>The group's inner member repeater, or null if not realised.</summary>
    private ItemsRepeater? InnerRepeaterOf(DeviceGroupCard group)
    {
        var blockIndex = ViewModel.Blocks.IndexOf(group);
        return blockIndex >= 0 && DevicesRepeater.TryGetElement(blockIndex) is FrameworkElement blockEl
            ? FindDescendant<ItemsRepeater>(blockEl)
            : null;
    }

    /// <summary>The member insertion index ([0, memberCount]) for a pointer, using the inner layout's
    /// stable no-gap geometry (so an open gap / phantom doesn't make the answer jitter).</summary>
    private int InnerInsertionIndex(ItemsRepeater inner, WrapByRowLayout layout, Point point)
    {
        var local = DevicesRepeater.TransformToVisual(inner).TransformPoint(point);
        return layout.GetInsertionIndex(local);
    }

    /// <summary>The member endpoint id the pointer sits before within <paramref name="group"/> (null =
    /// at the end), skipping <paramref name="draggedMemberId"/> if set (within-group reorder).</summary>
    private string? MemberAnchorBefore(DeviceGroupCard group, Point point, string? draggedMemberId)
    {
        if (InnerRepeaterOf(group) is not { Layout: WrapByRowLayout layout } inner) return null;
        var raw = InnerInsertionIndex(inner, layout, point);
        for (var k = raw; k < group.Members.Count; k++)
        {
            var id = group.Members[k].Endpoint.Id;
            if (draggedMemberId is null || !string.Equals(id, draggedMemberId, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>Switches which inner layout is showing a member gap / phantom, clearing the previous.</summary>
    private void SetActiveInnerGap(WrapByRowLayout layout)
    {
        if (ReferenceEquals(_activeInnerLayout, layout)) return;
        _activeInnerLayout?.ClearReorderState();
        _activeInnerLayout = layout;
    }

    private void ClearActiveInnerGap()
    {
        _activeInnerLayout?.ClearReorderState();
        _activeInnerLayout = null;
    }

    /// <summary>Attaches the implicit slide animation to a group's member elements so they bump
    /// smoothly when the gap / phantom moves. One inner repeater is animated per drag.</summary>
    private void EnsureInnerAnimations(ItemsRepeater inner)
    {
        if (ReferenceEquals(_animatedInner, inner)) return;
        ClearInnerAnimations();
        _animatedInner = inner;
        var count = inner.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (inner.TryGetElement(i) is UIElement el) ApplyReorderAnimation(el, true);
        }
    }

    private void ClearInnerAnimations()
    {
        if (_animatedInner is null) return;
        var count = _animatedInner.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (_animatedInner.TryGetElement(i) is UIElement el) ApplyReorderAnimation(el, false);
        }
        _animatedInner = null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static string? PageBlockId(object block) => block switch
    {
        DeviceCard card => card.Endpoint.Id,
        DeviceGroupCard group => group.Id,
        _ => null,
    };

    private DeviceGroupCard? FindGroupOf(DeviceCard card)
    {
        foreach (var block in ViewModel.Blocks)
        {
            if (block is DeviceGroupCard group && group.Members.Contains(card)) return group;
        }
        return null;
    }

    /// <summary>Inner 40% of a rect counts as "centre" (30% inset each side); the surrounding frame
    /// reads as reorder so the two gestures don't fight at the boundary.</summary>
    private static bool IsCentreZone(Point p, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return false;
        var insetX = r.Width * 0.3;
        var insetY = r.Height * 0.3;
        return p.X >= r.Left + insetX && p.X <= r.Right - insetX
            && p.Y >= r.Top + insetY && p.Y <= r.Bottom - insetY;
    }

    /// <summary>True when the pointer sits in the join zone of a group other than <see cref="_draggedCardGroup"/>
    /// (used to move a member from one group into another). Out params give the target group + its block index.</summary>
    private bool TryResolveOtherGroupJoin(Point point, out DeviceGroupCard group, out int index)
    {
        index = Layout!.GetBlockIndexAt(point);
        if (index >= 0 && index < ViewModel.Blocks.Count
            && ViewModel.Blocks[index] is DeviceGroupCard candidate
            && !ReferenceEquals(candidate, _draggedCardGroup)
            && ResolveGroupIntent(index, point) == GroupDropIntent.Join)
        {
            group = candidate;
            return true;
        }
        group = null!;
        index = -1;
        return false;
    }

    private enum GroupDropIntent { Before, Join, After }

    /// <summary>For a lone card dragged over a group section: the thin top strip (its title band) =
    /// insert before, the bottom strip = insert after, the middle = join. The top strip is the only
    /// way to drop a card above a first-in-row group (nothing sits above it to aim at).</summary>
    private GroupDropIntent ResolveGroupIntent(int groupIndex, Point point)
    {
        var r = Layout!.GetContentRect(groupIndex);
        if (r.Height <= 0) return GroupDropIntent.Join;
        var edge = Math.Min(28.0, r.Height * 0.25);
        if (point.Y < r.Top + edge) return GroupDropIntent.Before;
        if (point.Y > r.Bottom - edge) return GroupDropIntent.After;
        return GroupDropIntent.Join;
    }

    private void SetCreateTarget(DeviceCard card)
    {
        if (ReferenceEquals(_createTarget, card)) return;
        ClearCreateTarget();
        _createTarget = card;
        card.IsGroupDropTarget = true;
    }

    private void ClearCreateTarget()
    {
        if (_createTarget is null) return;
        _createTarget.IsGroupDropTarget = false;
        _createTarget = null;
    }

    private void SetJoinTarget(DeviceGroupCard group)
    {
        if (ReferenceEquals(_joinTarget, group)) return;
        ClearJoinTarget();
        _joinTarget = group;
        group.IsJoinTarget = true;
    }

    private void ClearJoinTarget()
    {
        if (_joinTarget is null) return;
        _joinTarget.IsJoinTarget = false;
        _joinTarget = null;
    }

    private void ClearHighlights()
    {
        ClearCreateTarget();
        ClearJoinTarget();
    }

    /// <summary>Confirms a group disband (removing this member leaves only one). Returns true to proceed.</summary>
    private async Task<bool> ConfirmDisbandAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Disband group?",
            Content = "Removing this device leaves the group with a single device, so the group will be disbanded.",
            PrimaryButtonText = "Disband",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Compacts a raw "insert before block index" into the source-excluded space the gap and
    /// <see cref="HomeViewModel.ReorderBlock"/> both use.</summary>
    private int ToCompactIndex(int raw, int source)
    {
        var compact = raw > source ? raw - 1 : raw;
        return Math.Clamp(compact, 0, Math.Max(0, ViewModel.Blocks.Count - 1));
    }

    /// <summary>Shows a drag caption (e.g. "Move", "Move group") on the OS drag cursor.</summary>
    private static void SetDragCaption(DragEventArgs e, string caption)
    {
        e.DragUIOverride.Caption = caption;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private static void ApplyReorderAnimation(UIElement element, bool enable)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!enable)
        {
            visual.ImplicitAnimations = null;
            return;
        }

        var compositor = visual.Compositor;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
        offset.Duration = TimeSpan.FromMilliseconds(220);

        var animations = compositor.CreateImplicitAnimationCollection();
        animations["Offset"] = offset;
        visual.ImplicitAnimations = animations;
    }

    /// <summary>Gives every realised block the implicit Offset slide so ANY layout re-arrange glides:
    /// a reflow (a card's apps row appears and it grows), a device added / removed, a card shown /
    /// hidden, a group forming / disbanding, or a drag reorder. The implicit is attached AFTER the
    /// element's first (or recycle-reuse) arrange, never during it, so a freshly realised or recycled
    /// card snaps into place instead of sliding in from the origin or its previous slot - while every
    /// later move animates. Detaching on (re)prepare is also what keeps scrolling crisp: a card
    /// recycled onto a new item is repositioned without the implicit, so it never lags the scroll.</summary>
    private void OnBlockElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        var element = args.Element;
        ApplyReorderAnimation(element, false);   // off for the imminent placement arrange
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ApplyReorderAnimation(element, true));   // on once placed, for every later move
    }

    /// <summary>Attaches a Composition implicit Offset animation to an app chip's container the first
    /// time it renders, so a re-sort (active/idle tiering) or a sibling appearing/leaving slides the
    /// chips to their new spots instead of popping. Offset ONLY - no opacity, so a recycled container
    /// can't come back stuck transparent (the bug an opacity hide animation caused). Attached after the
    /// first arrange (Loaded), so a chip's first appearance is instant with no slide-from-origin. The
    /// animation lives on the container the WrapPanel arranges, not the template root.</summary>
    private void OnAppChipLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement border) return;
        if (VisualTreeHelper.GetParent(border) is not UIElement container) return;

        var visual = ElementCompositionPreview.GetElementVisual(container);
        var compositor = visual.Compositor;

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
        offset.Duration = TimeSpan.FromMilliseconds(220);

        var implicits = compositor.CreateImplicitAnimationCollection();
        implicits["Offset"] = offset;
        visual.ImplicitAnimations = implicits;
    }

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

    /// <summary>Reveals the chip's "Terminate this app" item only while Shift is held as the context
    /// menu opens - an Explorer-style hidden power action. The terminate item is the one carrying the
    /// AppChip as its Tag; its base availability is gated by <see cref="AppChip.ShowProcessActions"/>
    /// so a System Sounds or closed chip never exposes it even with Shift down. Shift state is read at
    /// the current input message, which is the right-click that opened the menu.</summary>
    // CA1822 suppressed: XAML event hookup requires an instance method even though the body is static.
#pragma warning disable CA1822
    private void OnAppChipFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        foreach (var item in flyout.Items)
        {
            if (item is MenuFlyoutItem { Tag: AppChip chip } terminate)
            {
                terminate.Visibility = shiftDown && chip.ShowProcessActions
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }
#pragma warning restore CA1822

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

        // A card / group drag (our own) is positioned at the container (OnBlocksDragOver); bubble
        // immediately without touching the DataView so the blocking payload read only runs for chips.
        if (_draggedCard is not null || _draggedGroup is not null) return;

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

        // Card / group drops are committed at the container (OnBlocksDrop); bubble immediately.
        if (_draggedCard is not null || _draggedGroup is not null) return;

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
