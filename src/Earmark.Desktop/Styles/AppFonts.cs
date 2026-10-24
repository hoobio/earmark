using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.VisualTree;

namespace Earmark.Desktop.Styles;

public static class AppFonts
{
    private static readonly string[] EmojiFamilies = ["Segoe UI Emoji"];

    private static bool _registered;

    public static void UseGrayscaleText()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        // The drawn caption buttons sit beside the window under its host, outside the window's own text options.
        Control.LoadedEvent.AddClassHandler<TopLevel>((top, _) =>
        {
            foreach (var visual in top.GetSelfAndVisualAncestors())
            {
                TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Antialias);
            }
        });
    }

    // Avalonia 12.1.3 does not map FontWeight onto a variable font's wght axis, so Hanken Grotesk ships as static per-weight TTFs.
    public static AppBuilder UseEarmarkFonts(this AppBuilder builder) => builder
        .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(new Uri("fonts:Earmark"), new Uri("avares://Earmark/Assets/Fonts"))))
        .With(new FontManagerOptions
        {
            DefaultFamilyName = "fonts:Earmark#Hanken Grotesk",
            FontFallbacks = [.. EmojiFamilies.Select(family => new FontFallback { FontFamily = new FontFamily(family) })],
        });
}
