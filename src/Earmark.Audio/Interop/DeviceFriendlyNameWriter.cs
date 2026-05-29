using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Earmark.Audio.Interop;

/// <summary>
/// Writes the FriendlyName property of an MMDevice via IPropertyStore in read-write mode.
/// NAudio's PropertyStore wrapper opens read-only, so we drop to raw COM for the rename path.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class DeviceFriendlyNameWriter
{
    // PKEY_Device_FriendlyName: the "device name" you set in Sound control panel.
    private static readonly Guid FriendlyNameFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint FriendlyNamePid = 14;

    private const uint StgmReadWrite = 0x00000002;
    private const ushort VtLpwstr = 31;

    public static bool TrySetFriendlyName(string deviceId, string friendlyName, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(friendlyName);

        error = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = enumerator.GetDevice(deviceId, out device);
            if (hr < 0 || device is null)
            {
                error = $"GetDevice 0x{hr:X8}";
                return false;
            }

            hr = device.OpenPropertyStore(StgmReadWrite, out store);
            if (hr < 0 || store is null)
            {
                error = $"OpenPropertyStore 0x{hr:X8}";
                return false;
            }

            var key = new PropertyKey { FormatId = FriendlyNameFmtid, PropertyId = FriendlyNamePid };
            var pv = new PropVariant
            {
                Vt = VtLpwstr,
                Pointer = Marshal.StringToCoTaskMemUni(friendlyName),
            };
            try
            {
                hr = store.SetValue(ref key, ref pv);
                if (hr < 0)
                {
                    error = $"SetValue 0x{hr:X8}";
                    return false;
                }

                hr = store.Commit();
                if (hr < 0)
                {
                    error = $"Commit 0x{hr:X8}";
                    return false;
                }
                return true;
            }
            finally
            {
                _ = PropVariantClear(ref pv);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // VT_LPWSTR fits in the first 16 bytes after the header; the trailing field keeps the
    // struct at the 24-byte (32-bit) / 24-byte (64-bit aligned) PROPVARIANT size on x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Padding;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pNotify);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pNotify);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }
}
