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
    // exists on the machine so the ping matches the systray volume slider's audible blip
    // - that's "Windows Background.wav" (the same file Windows plays on slider release).
    private static readonly string[] CandidateWavNames =
    [
        "Windows Background.wav",
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
        ManualResetEventSlim? finished = null;
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

            finished = new ManualResetEventSlim(false);
            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            // Event-driven instead of a 20ms busy-poll. Guard the Set: PlaybackStopped can fire
            // again during output.Dispose() in the finally, after `finished` is disposed.
            output.PlaybackStopped += (_, _) => { try { finished.Set(); } catch (ObjectDisposedException) { } };
            output.Init(sampleProvider);
            output.Play();

            // Block this background task on the stop signal (no busy-poll) until playback ends,
            // capped by a safety deadline. WAV is typically ~200ms; 2s is generous.
            finished.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "PingPlayer.Play({DeviceId}) failed", deviceId);
        }
        finally
        {
            // Dispose `finished` last: output.Dispose() can raise a final PlaybackStopped whose
            // handler touches it.
            output?.Dispose();
            reader?.Dispose();
            device?.Dispose();
            finished?.Dispose();
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
