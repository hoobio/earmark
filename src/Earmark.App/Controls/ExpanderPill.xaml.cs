using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Earmark.App.Controls;

/// <summary>
/// A filled, rounded "pill" with arbitrary content on the left and a chevron on the right that
/// toggles <see cref="IsExpanded"/>. The chevron fills the pill's height so it reads as part of
/// the pill rather than a detached button. Shared by the first-rule chip on the Devices page and
/// the rule-row header on the Rules page so both expanders look and behave identically.
///
/// This is a custom control because a WinUI <see cref="Expander"/> can't stretch its header
/// content to fill the available width, and a nested <see cref="Button"/> swallows the first
/// click inside a ListView item (the item has to take focus first). The toggle is driven by
/// <see cref="UIElement.Tapped"/>, which fires on a single tap.
/// </summary>
[ContentProperty(Name = nameof(PillContent))]
public sealed partial class ExpanderPill : UserControl
{
    private static readonly string ChevronUp = new((char)0xE70E, 1);
    private static readonly string ChevronDown = new((char)0xE70D, 1);

    private readonly SolidColorBrush _transparent = new(Colors.Transparent);
    private bool _chevronHovered;

    public ExpanderPill()
    {
        InitializeComponent();
        PillRoot.CornerRadius = PillCornerRadius;
        ChevronHost.CornerRadius = ChevronCornerRadius;
        ApplyChevronRest();
        UpdateChevron();
    }

    /// <summary>The pill body, shown left of the chevron. Declared as the control's content,
    /// so it can be set as the child element in XAML.</summary>
    public static readonly DependencyProperty PillContentProperty = DependencyProperty.Register(
        nameof(PillContent), typeof(object), typeof(ExpanderPill),
        new PropertyMetadata(null, OnPillContentChanged));

    public object? PillContent
    {
        get => GetValue(PillContentProperty);
        set => SetValue(PillContentProperty, value);
    }

    /// <summary>Expanded state. Two-way by convention; the chevron glyph follows it.</summary>
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(ExpanderPill),
        new PropertyMetadata(false, OnIsExpandedChanged));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Whether the chevron is shown. Hide it when there is nothing to expand.</summary>
    public static readonly DependencyProperty ChevronVisibleProperty = DependencyProperty.Register(
        nameof(ChevronVisible), typeof(bool), typeof(ExpanderPill),
        new PropertyMetadata(true, OnChevronVisibleChanged));

    public bool ChevronVisible
    {
        get => (bool)GetValue(ChevronVisibleProperty);
        set => SetValue(ChevronVisibleProperty, value);
    }

    /// <summary>Chevron tooltip override. When null the control supplies "Expand" / "Collapse".</summary>
    public static readonly DependencyProperty ToggleTooltipProperty = DependencyProperty.Register(
        nameof(ToggleTooltip), typeof(string), typeof(ExpanderPill),
        new PropertyMetadata(null, OnToggleTooltipChanged));

    public string? ToggleTooltip
    {
        get => (string?)GetValue(ToggleTooltipProperty);
        set => SetValue(ToggleTooltipProperty, value);
    }

    /// <summary>When true (default), tapping anywhere on the pill toggles. When false only the
    /// chevron toggles, leaving the body free for its own click (e.g. navigate).</summary>
    public static readonly DependencyProperty ToggleOnBodyTapProperty = DependencyProperty.Register(
        nameof(ToggleOnBodyTap), typeof(bool), typeof(ExpanderPill),
        new PropertyMetadata(true));

    public bool ToggleOnBodyTap
    {
        get => (bool)GetValue(ToggleOnBodyTapProperty);
        set => SetValue(ToggleOnBodyTapProperty, value);
    }

    /// <summary>Pill fill. Defaults to the chip grey (the Devices chip is a pill on a card);
    /// set it transparent when the pill sits on its own card so it doesn't nest a second box.</summary>
    public static readonly DependencyProperty PillBackgroundProperty = DependencyProperty.Register(
        nameof(PillBackground), typeof(Brush), typeof(ExpanderPill),
        new PropertyMetadata(null, OnPillBackgroundChanged));

    public Brush? PillBackground
    {
        get => (Brush?)GetValue(PillBackgroundProperty);
        set => SetValue(PillBackgroundProperty, value);
    }

    /// <summary>Corner radius of the pill itself. Defaults to the chip's 4.</summary>
    public static readonly DependencyProperty PillCornerRadiusProperty = DependencyProperty.Register(
        nameof(PillCornerRadius), typeof(CornerRadius), typeof(ExpanderPill),
        new PropertyMetadata(new CornerRadius(4), OnPillCornerRadiusChanged));

    public CornerRadius PillCornerRadius
    {
        get => (CornerRadius)GetValue(PillCornerRadiusProperty);
        set => SetValue(PillCornerRadiusProperty, value);
    }

    /// <summary>Corner radius of the chevron box. Round only the right corners (e.g. "0,4,4,0")
    /// so the chevron sits flush against the right edge of its container, matching that
    /// container's radius (4 for the Devices chip, 8 for a Rules card).</summary>
    public static readonly DependencyProperty ChevronCornerRadiusProperty = DependencyProperty.Register(
        nameof(ChevronCornerRadius), typeof(CornerRadius), typeof(ExpanderPill),
        new PropertyMetadata(new CornerRadius(0, 4, 4, 0), OnChevronCornerRadiusChanged));

    public CornerRadius ChevronCornerRadius
    {
        get => (CornerRadius)GetValue(ChevronCornerRadiusProperty);
        set => SetValue(ChevronCornerRadiusProperty, value);
    }

    /// <summary>When true, the chevron's grey box only shows while collapsed (where it fills the
    /// card's edge like the Devices chip). Expanding drops the box to a plain glyph so the open
    /// editor isn't cluttered by a floating box on its right. Leave false for a standalone chip
    /// (Devices) whose pill never grows, so the chevron keeps its grey in both states.</summary>
    public static readonly DependencyProperty PlainChevronWhenExpandedProperty = DependencyProperty.Register(
        nameof(PlainChevronWhenExpanded), typeof(bool), typeof(ExpanderPill),
        new PropertyMetadata(false, OnPlainChevronWhenExpandedChanged));

    public bool PlainChevronWhenExpanded
    {
        get => (bool)GetValue(PlainChevronWhenExpandedProperty);
        set => SetValue(PlainChevronWhenExpandedProperty, value);
    }

    private static void OnPillContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpanderPill)d).ContentHost.Content = e.NewValue;

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ExpanderPill)d;
        pill.UpdateChevron();
        pill.ApplyChevronRest();
    }

    private static void OnChevronVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpanderPill)d).ChevronHost.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;

    private static void OnToggleTooltipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpanderPill)d).UpdateChevron();

    private static void OnPillBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is Brush brush) ((ExpanderPill)d).PillRoot.Background = brush;
    }

    private static void OnPillCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpanderPill)d).PillRoot.CornerRadius = (CornerRadius)e.NewValue;

    private static void OnChevronCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpanderPill)d).ChevronHost.CornerRadius = (CornerRadius)e.NewValue;

    private static void OnPlainChevronWhenExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpanderPill)d).ApplyChevronRest();

    /// <summary>Sets the chevron's resting fill: a grey edge-fill box, or a plain (transparent)
    /// glyph when expanded and <see cref="PlainChevronWhenExpanded"/> is set. Skipped while
    /// hovered so it doesn't stomp the hover brush.</summary>
    private void ApplyChevronRest()
    {
        if (_chevronHovered) return;
        ChevronHost.Background = PlainChevronWhenExpanded && IsExpanded
            ? _transparent
            : (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"];
    }

    private void UpdateChevron()
    {
        ChevronIcon.Glyph = IsExpanded ? ChevronUp : ChevronDown;
        ToolTipService.SetToolTip(ChevronHost, ToggleTooltip ?? (IsExpanded ? "Collapse" : "Expand"));
    }

    private void OnChevronTapped(object sender, TappedRoutedEventArgs e)
    {
        IsExpanded = !IsExpanded;
        e.Handled = true; // stop the pill-body handler from toggling back
    }

    private void OnPillTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ToggleOnBodyTap || e.Handled) return;
        IsExpanded = !IsExpanded;
    }

    private void OnChevronPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _chevronHovered = true;
        ChevronHost.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    }

    private void OnChevronPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _chevronHovered = false;
        ApplyChevronRest();
    }
}
