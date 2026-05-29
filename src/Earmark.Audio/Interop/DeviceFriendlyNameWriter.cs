using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Earmark.Audio.Interop;

/// <summary>
/// DORMANT - audio-endpoint rename is not achievable from a third-party app on current Windows.
/// This writes PKEY_Device_FriendlyName via <see cref="IPolicyConfigVista.SetPropertyValue"/> on
/// CPolicyConfigClient, brokered to the Windows Audio Service (the same path that lets our
/// non-elevated default-device rules work). Empirically (Win11 26200) it still returns
/// E_ACCESSDENIED at every privilege level, as does a direct IMMDevice/IPropertyStore SetValue:
/// the service permits SetDefaultEndpoint but refuses a property write from a non-Settings caller.
/// The signature is verified correct (the call reaches the service - past RPC_X_BAD_STUB_DATA),
/// so this is kept as a known-good starting point if a future Windows build or capability allows
/// the write. Callers (the rename action, the Wave Link name reconciler) are currently shelved.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class DeviceFriendlyNameWriter
{
    // PKEY_Device_FriendlyName: the "device name" you set in the Sound control panel.
    private static readonly Guid FriendlyNameFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint FriendlyNamePid = 14;
    private const ushort VtLpwstr = 31;

    public static bool TrySetFriendlyName(string deviceId, string friendlyName, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(friendlyName);

        error = null;
        IPolicyConfigVista? policy = null;
        var pv = new PropVariant
        {
            Vt = VtLpwstr,
            Pointer = Marshal.StringToCoTaskMemUni(friendlyName),
        };
        try
        {
            var type = Type.GetTypeFromCLSID(typeof(CPolicyConfigClient).GUID)
                ?? throw new InvalidOperationException("CPolicyConfigClient CLSID is not registered");
            policy = Activator.CreateInstance(type) as IPolicyConfigVista;
            if (policy is null)
            {
                error = "CPolicyConfigClient does not implement IPolicyConfigVista";
                return false;
            }

            var key = new PropertyKey { FormatId = FriendlyNameFmtid, PropertyId = FriendlyNamePid };
            // fxStore: false -> the device property store (the FriendlyName), not the FX store.
            var hr = policy.SetPropertyValue(deviceId, fxStore: false, ref key, ref pv);
            if (hr < 0)
            {
                error = $"SetPropertyValue 0x{hr:X8}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            _ = PropVariantClear(ref pv);
            if (policy is not null)
            {
                Marshal.ReleaseComObject(policy);
            }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
