using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Styling;

using Earmark.Desktop.Services;

namespace Earmark.Desktop.Views;

public sealed class RulesExpander : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<RulesExpander, bool>(nameof(IsExpanded));

    private int _version;

    static RulesExpander() => ClipToBoundsProperty.OverrideDefaultValue<RulesExpander>(true);

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
        {
            _ = SlideAsync(change.GetNewValue<bool>());
        }
        else if (change.Property == ChildProperty && Child is { } child)
        {
            child.IsVisible = IsExpanded;
        }
    }

    private async Task SlideAsync(bool expand)
    {
        if (Child is not { } child) return;
        var version = ++_version;
        if (!Motion.Enabled || Bounds.Width <= 0)
        {
            child.IsVisible = expand;
            ClearValue(HeightProperty);
            return;
        }

        var from = Bounds.Height;
        child.IsVisible = true;
        child.Measure(new Size(Bounds.Width, double.PositiveInfinity));
        var to = expand ? child.DesiredSize.Height : 0;
        Height = to;
        var slide = new Animation
        {
            Duration = Motion.Duration,
            Easing = Motion.Standard,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(HeightProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(HeightProperty, to) } },
            },
        };
        await slide.RunAsync(this);
        if (version != _version) return;

        child.IsVisible = expand;
        ClearValue(HeightProperty);
    }
}
