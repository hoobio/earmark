namespace Earmark.App.Services;

/// <summary>
/// Owns the Devices-page "defaults": the starter groups + visibility seeded on a fresh install, and
/// the user-invoked reset that restores them. Mutates the shared <see cref="Settings.AppSettings"/>
/// in place and raises <see cref="DefaultsApplied"/> so the Devices page rebuilds.
/// </summary>
public interface IDeviceDefaultsService
{
    /// <summary>
    /// Seeds the starter device groups + two disabled example rules when the install is a blank slate
    /// (no rules and no Devices-page customisation). A no-op for any configured install, so it can
    /// never wipe an existing layout. No persisted "seeded" flag - emptiness is the signal.
    /// </summary>
    Task SeedDefaultsIfEmptyAsync(CancellationToken ct = default);

    /// <summary>
    /// Restores the Devices page to its default groups, block order, and visibility. Never touches
    /// rules or any non-Devices setting (theme, tray, peak meter, Wave Link, window size).
    /// </summary>
    Task ResetDeviceLayoutAsync(CancellationToken ct = default);

    /// <summary>Raised after defaults are applied (seed or reset) so the Devices page can refresh.</summary>
    event EventHandler? DefaultsApplied;
}
