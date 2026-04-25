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
    }

    public void SetDefaultEndpointForApp(string sessionIdentifier, string endpointId, RoleScope role, EndpointFlow flow)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionIdentifier);
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        var processId = ResolveProcessId(sessionIdentifier);
        var dataFlow = ToDataFlow(flow);
        var persistedId = BuildPersistedId(endpointId, dataFlow);

        ApplyToRoles(role, eRole =>
        {
            try
            {
                Invoke((win11, win10) =>
                {
                    var hr = win11 is not null
                        ? win11.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, persistedId)
                        : win10!.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, persistedId);

                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }
                });

                _logger.LogInformation(
                    "Set default {Flow}/{Role} endpoint for pid {Pid} -> {EndpointId}",
                    flow, eRole, processId, endpointId);
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
                var hr = win11 is not null
                    ? win11.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, string.Empty)
                    : win10!.SetPersistedDefaultAudioEndpoint(processId, dataFlow, eRole, string.Empty);

                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            });
        });
    }

    private void Invoke(Action<IAudioPolicyConfigFactoryWin11?, IAudioPolicyConfigFactoryWin10?> action)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(typeof(CPolicyConfigClient).GUID)!);
        try
        {
            if (_isWin11 && instance is IAudioPolicyConfigFactoryWin11 win11)
            {
                action(win11, null);
                return;
            }

            if (instance is IAudioPolicyConfigFactoryWin10 win10)
            {
                action(null, win10);
                return;
            }

            throw new InvalidOperationException("IAudioPolicyConfigFactory is not available on this OS.");
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
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
