namespace Earmark.App.Settings;

/// <summary>How the now-playing strip blurs low-resolution artwork to fill its backdrop. Only used
/// when the source art is too small to upscale sharply (see the now-playing research doc); high-res
/// art fills sharp regardless.</summary>
public enum NowPlayingBackdropBlurMode
{
    /// <summary>True Gaussian blur via Win2D. Best-looking frosted backdrop.</summary>
    Gaussian,

    /// <summary>Decode tiny and let the cover-fill upscale soften it. Cheap, no GPU effect graph.</summary>
    Downscale,
}
