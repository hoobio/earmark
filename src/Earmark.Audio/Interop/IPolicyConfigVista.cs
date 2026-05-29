using System.Runtime.InteropServices;

namespace Earmark.Audio.Interop;

[ComImport]
[Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();

    // Brokered to the Windows Audio Service (like SetDefaultEndpoint), so writing an endpoint
    // property here does NOT require the caller to be elevated - the service does the privileged
    // store write. Setting PKEY_Device_FriendlyName is how a device gets renamed without the
    // E_ACCESSDENIED that a direct IPropertyStore::SetValue hits.
    [PreserveSig]
    int SetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        [MarshalAs(UnmanagedType.Bool)] bool fxStore,
        [In] ref PropertyKey key,
        [In] ref PropVariant value);

    [PreserveSig]
    int SetDefaultEndpoint(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        ERole role);

    [PreserveSig] int SetEndpointVisibility();
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

// VT_LPWSTR fits the pointer field; the trailing IntPtr keeps the struct at the native
// PROPVARIANT size on x64.
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Pointer;
    public IntPtr Padding;
}
