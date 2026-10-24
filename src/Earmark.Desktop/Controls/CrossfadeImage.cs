using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;

using Earmark.Desktop.Services;

namespace Earmark.Desktop.Controls;

public sealed class CrossfadeImage : TransitioningContentControl
{
    public CrossfadeImage()
    {
        PageTransition = Motion.Enabled ? new CrossFade(Motion.Duration) : null;
        ContentTemplate = new FuncDataTemplate<IImage>((image, _) =>
        {
            var view = new Image { Source = image, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapInterpolationMode(view, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            return view;
        });
    }

    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);

    // A background layer: under an unbounded height a UniformToFill image measures to its own aspect ratio and would stretch the card.
    protected override Size MeasureOverride(Size availableSize)
    {
        _ = base.MeasureOverride(availableSize);
        return default;
    }
}
