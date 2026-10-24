using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class DevicesRegistration
{
    public static IServiceCollection AddDevicesArea(this IServiceCollection services) =>
        services.AddSingleton<DevicesPage>();
}
