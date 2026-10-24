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
        ContentTemplate = new FuncDataTemplate<IImage>((image, _) => new Image { Source = image, Stretch = Stretch.UniformToFill });
    }

    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);
}
