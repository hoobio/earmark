using System.Runtime.InteropServices;

namespace Earmark.Audio.Interop;

// IAudioPolicyConfigFactory is undocumented. The Windows 11 (22000+) IID adds several methods
// before the persisted-default-endpoint methods. Settings > Sound > Volume Mixer uses the same
// interface to pin per-app default endpoints.
[ComImport]
[Guid("ab3d4648-e242-459f-b02f-541c70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
internal interface IAudioPolicyConfigFactoryWin11
{
    int __reserved_00();
    int __reserved_01();
    int __reserved_02();
    int __reserved_03();
    int __reserved_04();
    int __reserved_05();
    int __reserved_06();
    int __reserved_07();
    int __reserved_08();
    int __reserved_09();
    int __reserved_10();
    int __reserved_11();
    int __reserved_12();
    int __reserved_13();
    int __reserved_14();
    int __reserved_15();
    int __reserved_16();
    int __reserved_17();
    int __reserved_18();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(
        uint processId,
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.HString)] string deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(
        uint processId,
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.HString)] out string deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}

// Windows 10 19H1 (1903) - 21H2 layout had fewer reserved methods.
[ComImport]
[Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
internal interface IAudioPolicyConfigFactoryWin10
{
    int __reserved_00();
    int __reserved_01();
    int __reserved_02();
    int __reserved_03();
    int __reserved_04();
    int __reserved_05();
    int __reserved_06();
    int __reserved_07();
    int __reserved_08();
    int __reserved_09();
    int __reserved_10();
    int __reserved_11();
    int __reserved_12();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(
        uint processId,
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.HString)] string deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(
        uint processId,
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.HString)] out string deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}
