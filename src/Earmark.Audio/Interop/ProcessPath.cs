using System.Runtime.InteropServices;

namespace Earmark.Audio.Interop;

internal static class ProcessPath
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    public static string TryGet(uint pid)
    {
        if (pid == 0)
        {
            return string.Empty;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return new string(buffer, 0, (int)size);
            }

            return string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
