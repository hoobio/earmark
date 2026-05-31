using System.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Earmark.App.Controls;

/// <summary>An item that wants the <see cref="WrapPanel"/> to arrange it by a sort key rather than by
/// its position in the items collection. Lower <see cref="WrapOrder"/> arranges earlier. The data item
/// must raise <see cref="INotifyPropertyChanged"/> for <c>WrapOrder</c> so the panel re-arranges when
/// the key changes.</summary>
public interface IWrapOrdered
{
    int WrapOrder { get; }
}

/// <summary>
/// Minimal non-virtualizing wrap panel: lays children left-to-right, wrapping to a new row when the
/// next child would overflow the available width. Used as the apps-row ItemsPanel so the chip host
/// is a plain <see cref="Panel"/> - that lets each chip be a real element driven by a Composition
/// implicit Offset animation, which an <c>ItemsRepeater</c> + virtualizing layout can't do. Chip
/// counts per card are tiny, so dropping virtualization here costs nothing.
/// </summary>
/// <remarks>
/// Children are arranged by their data item's <see cref="IWrapOrdered.WrapOrder"/> (a stable sort),
/// NOT by collection order. A re-sort therefore re-positions the SAME containers - their implicit
/// Offset animation glides every moved chip to its new slot. Reordering the underlying collection
/// instead (an <c>ObservableCollection.Move</c>) makes the host <c>ItemsControl</c> recreate the moved
/// chip's container, which lands fresh at its destination with no slide. The panel watches each
/// child's <c>WrapOrder</c> and re-arranges when it changes.
/// </remarks>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    // Child -> the INotifyPropertyChanged we've hooked on its DataContext, so a WrapOrder change
    // re-arranges (without it the panel would only re-sort when the collection itself changes).
    private readonly Dictionary<FrameworkElement, INotifyPropertyChanged?> _watched = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        ReconcileWatchers();

        var maxWidth = availableSize.Width;
        if (double.IsInfinity(maxWidth) || double.IsNaN(maxWidth)) maxWidth = double.MaxValue;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        double x = 0, y = 0, rowHeight = 0, maxRowWidth = 0;
        foreach (var child in OrderedChildren())
        {
            var d = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + d.Width > maxWidth)
            {
                // Next child overflows the current row - wrap.
                maxRowWidth = System.Math.Max(maxRowWidth, x);
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            if (x > 0) x += HorizontalSpacing;
            x += d.Width;
            rowHeight = System.Math.Max(rowHeight, d.Height);
        }

        maxRowWidth = System.Math.Max(maxRowWidth, x);
        var measuredWidth = double.IsInfinity(availableSize.Width) ? maxRowWidth : System.Math.Min(maxRowWidth, availableSize.Width);
        return new Size(measuredWidth, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = finalSize.Width;
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in OrderedChildren())
        {
            var d = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + d.Width > maxWidth)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            if (x > 0) x += HorizontalSpacing;
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width;
            rowHeight = System.Math.Max(rowHeight, d.Height);
        }

        return finalSize;
    }

    /// <summary>Children in arrange order: by their data item's <see cref="IWrapOrdered.WrapOrder"/>
    /// (a stable sort, so equal keys keep collection order). Items that aren't ordered sort first.</summary>
    private IEnumerable<UIElement> OrderedChildren() =>
        Children.OrderBy(WrapOrderOf);

    private static int WrapOrderOf(UIElement child) =>
        (child as FrameworkElement)?.DataContext is IWrapOrdered ordered ? ordered.WrapOrder : 0;

    // ---- WrapOrder change watching ----

    private void ReconcileWatchers()
    {
        // Drop watchers for children no longer present.
        if (_watched.Count > 0)
        {
            List<FrameworkElement>? stale = null;
            foreach (var fe in _watched.Keys)
            {
                if (!ContainsChild(fe)) (stale ??= new()).Add(fe);
            }
            if (stale is not null)
            {
                foreach (var fe in stale) Unwatch(fe);
            }
        }

        // Watch any new child (the container's DataContext is the chip).
        foreach (var child in Children)
        {
            if (child is FrameworkElement fe && !_watched.ContainsKey(fe))
            {
                fe.DataContextChanged += OnChildDataContextChanged;
                var inpc = fe.DataContext as INotifyPropertyChanged;
                if (inpc is not null) inpc.PropertyChanged += OnChildPropertyChanged;
                _watched[fe] = inpc;
            }
        }
    }

    private bool ContainsChild(UIElement element)
    {
        foreach (var child in Children)
        {
            if (ReferenceEquals(child, element)) return true;
        }
        return false;
    }

    private void Unwatch(FrameworkElement fe)
    {
        fe.DataContextChanged -= OnChildDataContextChanged;
        if (_watched.TryGetValue(fe, out var inpc) && inpc is not null)
        {
            inpc.PropertyChanged -= OnChildPropertyChanged;
        }
        _watched.Remove(fe);
    }

    private void OnChildDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_watched.TryGetValue(sender, out var previous) && previous is not null)
        {
            previous.PropertyChanged -= OnChildPropertyChanged;
        }
        var inpc = args.NewValue as INotifyPropertyChanged;
        if (inpc is not null) inpc.PropertyChanged += OnChildPropertyChanged;
        _watched[sender] = inpc;
        InvalidateArrange();
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(IWrapOrdered.WrapOrder))
        {
            InvalidateArrange();
        }
    }
}
