namespace Earmark.App.Settings;

/// <summary>How the Devices-page peak meter groups channels into bars.</summary>
public enum PeakMeterChannelMode
{
    /// <summary>Stacked per-channel bars (mono / L-R / L-Centre+LFE-R), the default.</summary>
    Split,

    /// <summary>One combined bar driven by the loudest channel.</summary>
    Combined,
}
