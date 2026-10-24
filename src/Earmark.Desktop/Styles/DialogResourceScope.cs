using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FluentAvalonia.UI.Controls;

namespace Earmark.Desktop.Styles;

// WinUI's ContentDialog resolves its controls' visual-state brushes (pointer over, pressed, checked, disabled) from generic.xaml rather than the app's overrides.
public static class DialogResourceScope
{
    private static readonly Lazy<ResourceDictionary> Resources = new(() =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Earmark/Styles/DialogResources.axaml")));

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        Control.LoadedEvent.AddClassHandler<FAContentDialog>((dialog, _) =>
        {
            foreach (var (key, value) in Resources.Value)
            {
                dialog.Resources.TryAdd(key, value);
            }
        });
    }
}
