using Earmark.Desktop.ViewModels;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;

namespace Earmark.Desktop.Hosting;

internal static class SessionsRegistration
{
    public static IServiceCollection AddSessionsArea(this IServiceCollection services) =>
        services.AddSingleton<SessionsViewModel>().AddSingleton<SessionsPage>();
}
