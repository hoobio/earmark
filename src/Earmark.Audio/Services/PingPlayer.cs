using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Earmark.Audio.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class PingPlayer
{
    // Built-in Windows sound effects, in order of preference. We pick the first one that
    // exists on the machine so the ping uses a familiar OS chime instead of a custom tone.
    private static readonly string[] CandidateWavNames =
    [
        "Windows Navigation Start.wav",
        "Windows Menu Command.wav",
        "Windows Pop-up Blocked.wav",
        "Windows Notify System Generic.wav",
        "Windows Default.wav",
    ];

    private const float PlaybackGain = 0.4f;

    public static void Play(string deviceId, MMDeviceEnumerator enumerator, ILogger logger)
    {
        MMDevice? device = null;
        WasapiOut? output = null;
        WaveFileReader? reader = null;
        try
        {
            device = enumerator.GetDevice(deviceId);
            if (device.DataFlow != DataFlow.Render)
            {
                device.Dispose();
                return;
            }

            var wavPath = ResolveSystemWav();
            if (wavPath is null)
            {
                logger.LogDebug("PingPlayer: no candidate Windows WAV file found; skipping");
                device.Dispose();
                return;
            }

            reader = new WaveFileReader(wavPath);
            var sampleProvider = new VolumeSampleProvider(reader.ToSampleProvider())
            {
                Volume = PlaybackGain,
            };

            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            output.Init(sampleProvider);
            output.Play();

            // Block this background task until playback finishes (or the safety deadline hits),
            // then dispose. WAV is typically ~200ms; 2s deadline is generous.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (output.PlaybackState == PlaybackState.Playing && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "PingPlayer.Play({DeviceId}) failed", deviceId);
        }
        finally
        {
            output?.Dispose();
            reader?.Dispose();
            device?.Dispose();
        }
    }

    private static string? ResolveSystemWav()
    {
        var mediaDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Media");
        if (!Directory.Exists(mediaDir)) return null;

        foreach (var name in CandidateWavNames)
        {
            var path = Path.Combine(mediaDir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
