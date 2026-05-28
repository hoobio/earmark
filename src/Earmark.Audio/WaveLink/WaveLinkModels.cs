using System.Text.Json.Serialization;

namespace Earmark.Audio.WaveLink;

public sealed record WaveLinkApplicationInfo(
    [property: JsonPropertyName("appID")] string AppId,
    [property: JsonPropertyName("operatingSystem")] string OperatingSystem,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("interfaceRevision")] int InterfaceRevision);

public sealed record WaveLinkMix(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("level")] double Level,
    [property: JsonPropertyName("isMuted")] bool IsMuted);

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

// Wave Link's getInputConfigs returns localMixer / streamMixer as positional tuples,
// not objects: index 0 is the mute bool, 1 is volume, 2 is filter-bypass. Carry them
// as JsonElement[] so we can pluck the bool without coupling to the order if Elgato
// ever flips it (read with TryGetBoolean / GetBoolean).
public sealed record WaveLinkInputConfig(
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("inputType")] string? InputType,
    [property: JsonPropertyName("isAvailable")] bool IsAvailable,
    [property: JsonPropertyName("localMixer")] IReadOnlyList<System.Text.Json.JsonElement>? LocalMixer,
    [property: JsonPropertyName("streamMixer")] IReadOnlyList<System.Text.Json.JsonElement>? StreamMixer);

public sealed record WaveLinkInputConfigsResult(
    [property: JsonPropertyName("inputConfigs")] IReadOnlyList<WaveLinkInputConfig> InputConfigs);
