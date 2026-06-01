using System.Diagnostics;
using System.Runtime.InteropServices;

using Earmark.Core.Models;

namespace Earmark.Audio.Interop;

internal static class ProcessOwnership
{
    public static uint ResolveAppProcessId(uint processId)
    {
        return ProcessRollup.ResolveHostedOwnerProcessId(processId, static pid =>
        {
            var parentPid = TryGetParentProcessId(pid);
            var path = ProcessPath.TryGet(pid);
            string name;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch
            {
                name = string.IsNullOrEmpty(path)
                    ? string.Empty
                    : Path.GetFileNameWithoutExtension(path);
            }

            return (parentPid, name, path);
        });
    }

    private static uint TryGetParentProcessId(uint processId)
    {
        try
        {
            var process = OpenProcess(ProcessAccessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                PROCESS_BASIC_INFORMATION info = default;
                var status = NtQueryInformationProcess(
                    process,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                    out _);

                return status == 0 && info.InheritedFromUniqueProcessId != IntPtr.Zero
                    ? unchecked((uint)info.InheritedFromUniqueProcessId.ToInt64())
                    : 0;
            }
            finally
            {
                _ = CloseHandle(process);
            }
        }
        catch
        {
            return 0;
        }
    }

    private const uint ProcessAccessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr objectHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        uint processInformationLength,
        out uint returnLength);
}
