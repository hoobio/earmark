using System.Runtime.InteropServices;

namespace Earmark.Audio.Interop;

internal static class HString
{
    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hString);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hString);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hString, out uint length);

    public static IntPtr Create(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return IntPtr.Zero;
        }

        WindowsCreateString(value, (uint)value.Length, out var handle);
        return handle;
    }

    public static string Read(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = WindowsGetStringRawBuffer(handle, out var length);
        return buffer == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
    }

    public static void Delete(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = WindowsDeleteString(handle);
        }
    }
}
