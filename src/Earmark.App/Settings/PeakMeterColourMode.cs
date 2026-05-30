namespace Earmark.App.Settings;

/// <summary>How the Devices-page peak meter paints its level bars.</summary>
public enum PeakMeterColourMode
{
    /// <summary>Green -&gt; amber -&gt; red with smooth gradient blends at the thresholds (default).</summary>
    Gradient,

    /// <summary>Green -&gt; amber -&gt; red with hard edges (no gradient blending).</summary>
    Blocks,

    /// <summary>A single user-chosen colour across the whole fill; no clip banding.</summary>
    Solid,

    /// <summary>No meter; the device card shows a standard volume slider.</summary>
    Off,
}
