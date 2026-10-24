using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Earmark.Desktop.Views;

public class SettingsCard : ContentControl
{
    public static readonly StyledProperty<object?> HeaderProperty = AvaloniaProperty.Register<SettingsCard, object?>(nameof(Header));

    public static readonly StyledProperty<object?> DescriptionProperty = AvaloniaProperty.Register<SettingsCard, object?>(nameof(Description));

    public static readonly StyledProperty<object?> HeaderIconProperty = AvaloniaProperty.Register<SettingsCard, object?>(nameof(HeaderIcon));

    public static readonly StyledProperty<string?> HeaderGlyphProperty = AvaloniaProperty.Register<SettingsCard, string?>(nameof(HeaderGlyph));

    public static readonly StyledProperty<string?> ActionGlyphProperty = AvaloniaProperty.Register<SettingsCard, string?>(nameof(ActionGlyph));

    public static readonly StyledProperty<bool> IsClickEnabledProperty = AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsClickEnabled));

    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent = RoutedEvent.Register<SettingsCard, RoutedEventArgs>(nameof(Click), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? HeaderIcon
    {
        get => GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }

    public string? HeaderGlyph
    {
        get => GetValue(HeaderGlyphProperty);
        set => SetValue(HeaderGlyphProperty, value);
    }

    public string? ActionGlyph
    {
        get => GetValue(ActionGlyphProperty);
        set => SetValue(ActionGlyphProperty, value);
    }

    public bool IsClickEnabled
    {
        get => GetValue(IsClickEnabledProperty);
        set => SetValue(IsClickEnabledProperty, value);
    }

    internal void RaiseClick() => RaiseEvent(new RoutedEventArgs(ClickEvent, this));

    static SettingsCard()
    {
        FocusableProperty.OverrideDefaultValue<SettingsCard>(false);
        IsClickEnabledProperty.Changed.AddClassHandler<SettingsCard>((card, e) => card.Focusable = e.GetNewValue<bool>());
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsClickEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            PseudoClasses.Set(":pressed", true);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!PseudoClasses.Contains(":pressed"))
        {
            return;
        }

        PseudoClasses.Set(":pressed", false);
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            RaiseClick();
        }

        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        PseudoClasses.Set(":pressed", false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsClickEnabled && e.Key is Key.Enter or Key.Space)
        {
            RaiseClick();
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        IsClickEnabled ? new ClickablePeer(this) : new CardPeer(this);

    private class CardPeer(SettingsCard owner) : ContentControlAutomationPeer(owner)
    {
        protected override string? GetNameCore() =>
            AutomationProperties.GetName(Owner) is { Length: > 0 } name ? name : (Owner as SettingsCard)?.Header as string;
    }

    private sealed class ClickablePeer(SettingsCard owner) : CardPeer(owner), IInvokeProvider
    {
        public void Invoke() => ((SettingsCard)Owner).RaiseClick();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
    }
}
