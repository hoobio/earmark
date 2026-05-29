using System.Text.Json.Serialization;

namespace Earmark.Audio.WaveLink;

public sealed record WaveLinkApplicationInfo(
    [property: JsonPropertyName("appID")] string AppId,
    [property: JsonPropertyName("operatingSystem")] string OperatingSystem,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("interfaceRevision")] int InterfaceRevision);

public sealed record WaveLinkMixImage(
    [property: JsonPropertyName("name")] string? Name);

public sealed record WaveLinkMix(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("level")] double Level,
    [property: JsonPropertyName("isMuted")] bool IsMuted,
    [property: JsonPropertyName("image")] WaveLinkMixImage? Image);

public sealed record WaveLinkMixesResult(
    [property: JsonPropertyName("mixes")] IReadOnlyList<WaveLinkMix> Mixes);

public sealed record WaveLinkOutput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isMuted")] bool IsMuted,
    [property: JsonPropertyName("level")] double Level,
    [property: JsonPropertyName("mixId")] string MixId);

public sealed record WaveLinkOutputDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("deviceType")] string DeviceType,
    [property: JsonPropertyName("outputs")] IReadOnlyList<WaveLinkOutput> Outputs);

public sealed record WaveLinkMainOutput(
    [property: JsonPropertyName("outputDeviceId")] string OutputDeviceId,
    [property: JsonPropertyName("outputId")] string OutputId);

public sealed record WaveLinkOutputDevicesResult(
    [property: JsonPropertyName("mainOutput")] WaveLinkMainOutput MainOutput,
    [property: JsonPropertyName("outputDevices")] IReadOnlyList<WaveLinkOutputDevice> OutputDevices);

// getInputDevices: hardware audio interfaces / mics WL can capture. Each device has one
// or more "inputs" (channels on that interface, e.g. SSL 2 has Input 1 / Input 2 / merged
// Input). Mute lives on the inner inputs[] item, not the device.
public sealed record WaveLinkInputDeviceInput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isMuted")] bool IsMuted);

public sealed record WaveLinkInputDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("deviceType")] string? DeviceType,
    [property: JsonPropertyName("inputs")] IReadOnlyList<WaveLinkInputDeviceInput> Inputs);

public sealed record WaveLinkInputDevicesResult(
    [property: JsonPropertyName("inputDevices")] IReadOnlyList<WaveLinkInputDevice> InputDevices);

// getChannels: the mixer strips shown down the left of Wave Link (Game, Comms, Media, SFX,
// System, plus any hardware mic). Each carries an "image" object: imgData is a base64 PNG
// of the channel's coloured tile (the accent colour Wave Link assigns), isAppIcon true when
// it's a real application icon rather than a built-in glyph.
public sealed record WaveLinkChannelImage(
    [property: JsonPropertyName("imgData")] string? ImgData,
    [property: JsonPropertyName("isAppIcon")] bool IsAppIcon);

public sealed record WaveLinkChannel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("image")] WaveLinkChannelImage? Image);

public sealed record WaveLinkChannelsResult(
    [property: JsonPropertyName("channels")] IReadOnlyList<WaveLinkChannel> Channels);
