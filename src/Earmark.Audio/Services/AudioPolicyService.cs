using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Earmark.Audio.Interop;
using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.Extensions.Logging;

namespace Earmark.Audio.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AudioPolicyService : IAudioPolicyService
{
    private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";
    private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string DEVINTERFACE_AUDIO_CAPTURE = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private readonly ILogger<AudioPolicyService> _logger;
    private readonly bool _isWin11;

    public AudioPolicyService(ILogger<AudioPolicyService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isWin11 = Environment.OSVersion.Version.Build >= 22000;
        _logger.LogInformation(
            "AudioPolicyService init: OS build {Build}, using {Layout} interface",
            Environment.OSVersion.Version.Build, _isWin11 ? "Win11" : "Win10");
    }

    public void SetDefaultEndpointForApp(string sessionIdentifier, string endpointId, RoleScope role, EndpointFlow flow)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionIdentifier);
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        var processId = ResolveProcessId(sessionIdentifier);
        var dataFlow = ToDataFlow(flow);
        var persistedId = BuildPersistedId(endpointId, dataFlow);

        _logger.LogInformation(
            "SetDefault entry: pid={Pid} flow={Flow} role={Role} endpointId={EndpointId} persistedId='{PersistedId}'",
            processId, flow, role, endpointId, persistedId);

        ApplyToRoles(role, eRole =>
        {
            try
            {
                Invoke((win11, win10) =>
                {
                    var hstring = HString.Create(persistedId);
                    try
                    {
                        var hr = win11 is not null
                            ? win11.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, hstring)
                            : win10!.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, hstring);

                        _logger.LogInformation(
                            "SetPersistedDefaultAudioEndpoint(pid={Pid}, flow={Flow}, role={Role}) HR=0x{HR:X8}",
                            processId, dataFlow, eRole, (uint)hr);

                        if (hr < 0)
                        {
                            Marshal.ThrowExceptionForHR(hr);
                        }
                    }
                    finally
                    {
                        HString.Delete(hstring);
                    }

                    IntPtr readback = IntPtr.Zero;
                    try
                    {
                        var verifyHr = win11 is not null
                            ? win11.GetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, out readback)
                            : win10!.GetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, out readback);

                        var readbackValue = HString.Read(readback);
                        _logger.LogInformation(
                            "Verify GetPersistedDefaultAudioEndpoint(pid={Pid}, flow={Flow}, role={Role}) HR=0x{HR:X8} value='{Value}'",
                            processId, dataFlow, eRole, (uint)verifyHr, readbackValue);
                    }
                    finally
                    {
                        HString.Delete(readback);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed setting default {Flow}/{Role} endpoint for pid {Pid}",
                    flow, eRole, processId);
                throw;
            }
        });
    }

    public void ClearDefaultEndpointForApp(string sessionIdentifier, RoleScope role, EndpointFlow flow)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionIdentifier);

        var processId = ResolveProcessId(sessionIdentifier);
        var dataFlow = ToDataFlow(flow);

        ApplyToRoles(role, eRole =>
        {
            Invoke((win11, win10) =>
            {
                var empty = HString.Create(string.Empty);
                try
                {
                    var hr = win11 is not null
                        ? win11.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, empty)
                        : win10!.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, empty);

                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }
                }
                finally
                {
                    HString.Delete(empty);
                }
            });
        });
    }

    private static readonly string[] CandidateClassNames =
    {
        "Windows.Media.Internal.AudioPolicyConfig",
        "Windows.Media.AudioPolicyConfig",
    };

    private static readonly Guid IID_AudioPolicyConfigFactoryWin11 = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid IID_AudioPolicyConfigFactoryWin10 = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    private void Invoke(Action<IAudioPolicyConfigFactoryWin11?, IAudioPolicyConfigFactoryWin10?> action)
    {
        Exception? lastError = null;
        foreach (var className in CandidateClassNames)
        {
            if (_isWin11)
            {
                try
                {
                    var iid = IID_AudioPolicyConfigFactoryWin11;
                    var factory = WinRtFactory.GetFactory(className, iid);
                    if (factory is IAudioPolicyConfigFactoryWin11 win11)
                    {
                        _logger.LogInformation("Activated factory '{Class}' as Win11 layout", className);
                        try
                        {
                            action(win11, null);
                            return;
                        }
                        finally
                        {
                            if (Marshal.IsComObject(win11))
                            {
                                Marshal.ReleaseComObject(win11);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Win11 factory activation failed for class '{Class}'", className);
                    lastError = ex;
                }
            }

            try
            {
                var iid = IID_AudioPolicyConfigFactoryWin10;
                var factory = WinRtFactory.GetFactory(className, iid);
                if (factory is IAudioPolicyConfigFactoryWin10 win10)
                {
                    _logger.LogInformation("Activated factory '{Class}' as Win10 layout", className);
                    try
                    {
                        action(null, win10);
                        return;
                    }
                    finally
                    {
                        if (Marshal.IsComObject(win10))
                        {
                            Marshal.ReleaseComObject(win10);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Win10 factory activation failed for class '{Class}'", className);
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "IAudioPolicyConfigFactory is not available on this OS (tried " +
            string.Join(", ", CandidateClassNames) + ")",
            lastError);
    }

    private static void ApplyToRoles(RoleScope scope, Action<ERole> action)
    {
        switch (scope)
        {
            case RoleScope.Multimedia:
                action(ERole.Multimedia);
                break;
            case RoleScope.Communications:
                action(ERole.Communications);
                break;
            case RoleScope.Console:
                action(ERole.Console);
                break;
            case RoleScope.All:
            default:
                action(ERole.Console);
                action(ERole.Multimedia);
                action(ERole.Communications);
                break;
        }
    }

    private static EDataFlow ToDataFlow(EndpointFlow flow) => flow switch
    {
        EndpointFlow.Capture => EDataFlow.Capture,
        _ => EDataFlow.Render,
    };

    private static string BuildPersistedId(string endpointId, EDataFlow flow)
    {
        var suffix = flow == EDataFlow.Render
            ? DEVINTERFACE_AUDIO_RENDER
            : DEVINTERFACE_AUDIO_CAPTURE;
        return MMDEVAPI_TOKEN + endpointId + suffix;
    }

    private static uint ResolveProcessId(string sessionIdentifier)
    {
        // Session identifiers from IAudioSessionControl2.GetSessionInstanceIdentifier embed the
        // owning PID in the form "...|...%b{pid}". When unavailable, fall back to 0 (which the OS
        // interprets as "all sessions" / system).
        if (uint.TryParse(sessionIdentifier, out var direct))
        {
            return direct;
        }

        return 0u;
    }
}
