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
}
