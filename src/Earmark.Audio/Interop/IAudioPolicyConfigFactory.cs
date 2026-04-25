using System.Runtime.InteropServices;

namespace Earmark.Audio.Interop;

// IAudioPolicyConfigFactory is undocumented. We declare it as IUnknown-based and explicitly list
// IInspectable's three methods (GetIids, GetRuntimeClassName, GetTrustLevel) as reserved entries
// because modern .NET does not support marshalling IInspectable interfaces directly. HSTRING
// arguments are passed as raw IntPtr and built via the HString helper.

[ComImport]
[Guid("ab3d4648-e242-459f-b02f-541c70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigFactoryWin11
{
    [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    [PreserveSig] int __reserved_00();
    [PreserveSig] int __reserved_01();
    [PreserveSig] int __reserved_02();
    [PreserveSig] int __reserved_03();
    [PreserveSig] int __reserved_04();
    [PreserveSig] int __reserved_05();
    [PreserveSig] int __reserved_06();
    [PreserveSig] int __reserved_07();
    [PreserveSig] int __reserved_08();
    [PreserveSig] int __reserved_09();
    [PreserveSig] int __reserved_10();
    [PreserveSig] int __reserved_11();
    [PreserveSig] int __reserved_12();
    [PreserveSig] int __reserved_13();
    [PreserveSig] int __reserved_14();
    [PreserveSig] int __reserved_15();
    [PreserveSig] int __reserved_16();
    [PreserveSig] int __reserved_17();
    [PreserveSig] int __reserved_18();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}

[ComImport]
[Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigFactoryWin10
{
    [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    [PreserveSig] int __reserved_00();
    [PreserveSig] int __reserved_01();
    [PreserveSig] int __reserved_02();
    [PreserveSig] int __reserved_03();
    [PreserveSig] int __reserved_04();
    [PreserveSig] int __reserved_05();
    [PreserveSig] int __reserved_06();
    [PreserveSig] int __reserved_07();
    [PreserveSig] int __reserved_08();
    [PreserveSig] int __reserved_09();
    [PreserveSig] int __reserved_10();
    [PreserveSig] int __reserved_11();
    [PreserveSig] int __reserved_12();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}
