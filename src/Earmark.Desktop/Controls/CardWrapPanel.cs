using Avalonia;
using Avalonia.Controls;

using Earmark.Core.Settings;
using Earmark.Desktop.ViewModels.Devices;

namespace Earmark.Desktop.Controls;

public sealed class CardWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<CardWrapPanel, double>(nameof(MinItemWidth), 320);
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<CardWrapPanel, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<CardHeightMode> HeightModeProperty = AvaloniaProperty.Register<CardWrapPanel, CardHeightMode>(nameof(HeightMode));

    static CardWrapPanel() => AffectsMeasure<CardWrapPanel>(MinItemWidthProperty, SpacingProperty, HeightModeProperty);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public CardHeightMode HeightMode { get => GetValue(HeightModeProperty); set => SetValue(HeightModeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        Glide.Arranged(this);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        if (double.IsInfinity(width))
        {
            width = MinItemWidth;
        }

        var columns = Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));
        var itemWidth = (width - ((columns - 1) * Spacing)) / columns;
        var y = 0.0;
        var row = new List<Control>();

        void FlushRow()
        {
            if (row.Count == 0)
            {
                return;
            }

            var height = row.Max(c => c.DesiredSize.Height);
            if (arrange)
            {
                for (var i = 0; i < row.Count; i++)
                {
                    var stretch = HeightMode switch
                    {
                        CardHeightMode.MatchRow => true,
                        CardHeightMode.Dynamic => false,
                        _ => row[i].DataContext is not DeviceCardViewModel { IsRulesExpanded: true },
                    };
                    row[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, stretch ? height : row[i].DesiredSize.Height));
                }
            }

            y += height + Spacing;
            row.Clear();
        }

        foreach (var child in Children.Where(c => c.IsVisible))
        {
            var full = child.DataContext is DeviceGroupViewModel;
            if (!arrange)
            {
                child.Measure(new Size(full ? width : itemWidth, double.PositiveInfinity));
            }

            if (full)
            {
                FlushRow();
                if (arrange)
                {
                    child.Arrange(new Rect(0, y, width, child.DesiredSize.Height));
                }

                y += child.DesiredSize.Height + Spacing;
                continue;
            }

            row.Add(child);
            if (row.Count == columns)
            {
                FlushRow();
            }
        }

        FlushRow();
        return new Size(width, Math.Max(0, y - Spacing));
    }
}
