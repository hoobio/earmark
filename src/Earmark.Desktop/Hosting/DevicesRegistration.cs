using Avalonia.Controls;

using Earmark.Desktop.Controls;
using Earmark.Desktop.Services;
using Earmark.Desktop.Services.Devices;
using Earmark.Desktop.ViewModels.Devices;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class DevicesRegistration
{
    public static IServiceCollection AddDevicesArea(this IServiceCollection services) =>
        services
            .AddSingleton<DeviceImages>()
            .AddSingleton<DevicesViewModel>()
            .AddSingleton<DevicesPage>()
            .AddSingleton<TaskbarMediaControls>()
            .AddKeyedSingleton<Func<IReadOnlyList<Control>>>("QuickControlsBlocks", (sp, _) => () => QuickControlsBlocks(sp.GetRequiredService<DevicesViewModel>()));

    private static IReadOnlyList<Control> QuickControlsBlocks(DevicesViewModel vm) =>
        [.. vm.QuickControlsProjection().Select(block =>
        {
            var panel = new StackPanel { Classes = { "QuickBlock" } };
            if (block.Title is { Length: > 0 } title)
            {
                panel.Children.Add(new TextBlock { Classes = { "BodyStrong", "GroupTitle" }, Text = title });
            }

            foreach (var card in block.Cards)
            {
                panel.Children.Add(new DeviceCard { Card = card, IsOverlay = true });
            }

            return (Control)panel;
        })];
}
