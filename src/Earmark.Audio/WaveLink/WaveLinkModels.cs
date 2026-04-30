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
