using System.Text.Json;

namespace Earmark.Audio.WaveLink;

internal static class WaveLinkPortDiscovery
{
    private const string WaveLinkPackageFamily = "Elgato.WaveLink_g54w8ztgkx496";

    public static string WsInfoFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        WaveLinkPackageFamily,
        "LocalState",
        "ws-info.json");

    public static int? TryReadPort()
    {
        if (!File.Exists(WsInfoFilePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(WsInfoFilePath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("port", out var portElement)
                && portElement.TryGetInt32(out var port)
                && port > 0)
            {
                return port;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }

        return null;
    }

    public static IEnumerable<int> FallbackPorts() => Enumerable.Range(1884, 10);
}
