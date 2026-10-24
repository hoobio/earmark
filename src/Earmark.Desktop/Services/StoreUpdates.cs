using System.Runtime.InteropServices;

using Avalonia.Threading;

using Earmark.Core.Services;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Windows.ApplicationModel.Store.Preview.InstallControl;
using Windows.Services.Store;

namespace Earmark.Desktop.Services;

internal sealed class StoreUpdates(IServiceProvider services, ILogger<StoreUpdates> logger) : IStoreUpdates
{
    private const string ProductId = "9N27KG5M9W9B";
    private const int RestartNoCrashHangReboot = 1 | 2 | 8;
    private static readonly Uri StoreUpdatesUri = new("ms-windows-store://downloadsandupdates");

    private StoreContext? _context;
    private IReadOnlyList<StorePackageUpdate> _updates = [];
    private Task? _install;

    public Task<bool> CheckAsync(bool manual) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var window = services.GetRequiredService<MainWindow>();
        // The Store scan can install the update and close the app on its own, so the restart is registered before it.
        RegisterRestart(window);
        await ScanAsync(manual);

        if (_context is null)
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, window.TryGetPlatformHandle()?.Handle ?? 0);
            _context = context;
        }

        _updates = await _context.GetAppAndOptionalStorePackageUpdatesAsync();
        var silent = _context.CanSilentlyDownloadStorePackageUpdates;
        logger.LogInformation("Store update check: {Count} update(s), silent install {Silent}", _updates.Count, silent);
        if (_updates.Count > 0 && silent)
        {
            _ = InstallAsync();
        }

        return _updates.Count > 0;
    });

    public Task InstallAsync() => _install is { IsCompleted: false } running
        ? running
        : _install = Dispatcher.UIThread.InvokeAsync(InstallCoreAsync);

    private async Task InstallCoreAsync()
    {
        var window = services.GetRequiredService<MainWindow>();
        if (_context is not { } context || _updates.Count == 0)
        {
            await window.Launcher.LaunchUriAsync(StoreUpdatesUri);
            return;
        }

        var silent = context.CanSilentlyDownloadStorePackageUpdates;
        RegisterRestart(window);
        try
        {
            var result = silent
                ? await context.TrySilentDownloadAndInstallStorePackageUpdatesAsync(_updates)
                : await context.RequestDownloadAndInstallStorePackageUpdatesAsync(_updates);
            logger.LogInformation("Store update: finished {State} (silent {Silent})", result.OverallState, silent);
            if (result.OverallState is StorePackageUpdateState.Completed || silent)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Store update install failed (silent {Silent})", silent);
            if (silent)
            {
                return;
            }
        }

        // The product page of an installed app has no update button, so a failed or cancelled install opens the Store's update list.
        await window.Launcher.LaunchUriAsync(StoreUpdatesUri);
    }

    private async Task ScanAsync(bool manual)
    {
        AppInstallManager manager;
        try
        {
            manager = new AppInstallManager();
            // Documented as needing a Microsoft-only capability, but it runs from a full-trust process with none declared (verified in Steward, 28 Sep 2026).
            var item = await manager.SearchForUpdatesAsync(ProductId, string.Empty);
            logger.LogInformation("Store scan for Earmark: {State}", item?.GetCurrentStatus().InstallState.ToString() ?? "no install item");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Store scan for Earmark failed");
            return;
        }

        if (!manual)
        {
            return;
        }

        try
        {
            // Only the all-apps scan refreshes a flight's catalogue; it also queues every other Store app's update, so it runs on Check now only (Steward, 29 Sep 2026).
            await manager.SearchForAllUpdatesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Store scan for all apps failed");
        }
    }

    private void RegisterRestart(MainWindow window)
    {
        var argument = window.IsVisible ? StartupRegistration.NoActivateArgument : StartupRegistration.TrayArgument;
        var result = RegisterApplicationRestart(argument, RestartNoCrashHangReboot);
        if (result != 0)
        {
            logger.LogWarning("RegisterApplicationRestart failed: 0x{Result:X8}", result);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);
}
