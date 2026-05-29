namespace Earmark.App.Settings;

/// <summary>
/// How a device card that maps to a Wave Link channel renders its icon tile. Mutually
/// exclusive by design: a full Wave Link icon already carries the channel's colour, so
/// tinting on top of it is meaningless.
/// </summary>
public enum WaveLinkChannelStyle
{
    /// <summary>Plain Fluent glyph + default tile - identical to non-Wave Link devices.</summary>
    Off,

    /// <summary>Fluent glyph on a tile tinted with the channel's accent colour (derived from
    /// the channel artwork's dominant pixel).</summary>
    Colours,

    /// <summary>The channel's own Wave Link bitmap replaces the Fluent glyph entirely.</summary>
    Icons,
}
