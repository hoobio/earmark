using Avalonia;
using Avalonia.Controls;

namespace Earmark.Desktop.Controls;

public sealed class GlidingWrapPanel : WrapPanel
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        Glide.Arranged(this);
        return size;
    }
}
