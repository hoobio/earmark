using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.Foundation;

namespace Earmark.App.Controls;

/// <summary>
/// A background image layer that cross-fades when its <see cref="Source"/> changes instead of swapping
/// instantly. Two stacked layers trade opacity over <see cref="FadeDuration"/>, so a now-playing track
/// change eases between artworks rather than cutting. The art is painted as a <see cref="Border"/>
/// background (an <see cref="ImageBrush"/>), not an <see cref="Image"/> element, so it's clipped to
/// <see cref="CornerRadius"/> the way a background is - matching the card's rounded corners - and so it
/// reports zero desired size and never inflates the layout it backs.
/// </summary>
public sealed class CrossfadeImage : Panel
{
    private readonly Border _a = new() { Opacity = 0 };
    private readonly Border _b = new() { Opacity = 0 };
    private readonly ImageBrush _brushA = new() { Stretch = Stretch.UniformToFill };
    private readonly ImageBrush _brushB = new() { Stretch = Stretch.UniformToFill };
    private Border _current;
    private Storyboard? _running;

    public CrossfadeImage()
    {
        _a.Background = _brushA;
        _b.Background = _brushB;
        Children.Add(_a);
        Children.Add(_b);
        _current = _a;
    }

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(CrossfadeImage), new PropertyMetadata(null, OnSourceChanged));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => _brushA.Stretch;
        set { _brushA.Stretch = value; _brushB.Stretch = value; }
    }

    public CornerRadius CornerRadius
    {
        get => _a.CornerRadius;
        set { _a.CornerRadius = value; _b.CornerRadius = value; }
    }

    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromMilliseconds(300);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CrossfadeImage)d).Crossfade(e.NewValue as ImageSource);

    private void Crossfade(ImageSource? next)
    {
        // Stop() halts the previous run before its Completed fires, so the captured 'outgoing' below
        // can't be cleared out from under a newer fade.
        _running?.Stop();
        var incoming = ReferenceEquals(_current, _a) ? _b : _a;
        var incomingBrush = ReferenceEquals(incoming, _a) ? _brushA : _brushB;
        var outgoing = _current;
        var outgoingBrush = ReferenceEquals(outgoing, _a) ? _brushA : _brushB;
        incomingBrush.ImageSource = next;

        var sb = new Storyboard();
        if (next is not null)
        {
            sb.Children.Add(Fade(incoming, 1));
            _current = incoming;
        }
        sb.Children.Add(Fade(outgoing, 0));
        sb.Completed += (_, _) => outgoingBrush.ImageSource = null;
        _running = sb;
        sb.Begin();
    }

    private DoubleAnimation Fade(UIElement target, double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = FadeDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(0, 0, finalSize.Width, finalSize.Height);
        foreach (var child in Children) child.Arrange(rect);
        return finalSize;
    }
}
