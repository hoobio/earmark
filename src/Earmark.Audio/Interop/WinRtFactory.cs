using System.Runtime.InteropServices;

namespace Earmark.Audio.Interop;

internal static class WinRtFactory
{
    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    public static object? GetFactory(string runtimeClassName, Guid iid)
    {
        var classNameHandle = HString.Create(runtimeClassName);
        try
        {
            RoGetActivationFactory(classNameHandle, ref iid, out var pFactory);
            if (pFactory == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.GetObjectForIUnknown(pFactory);
            }
            finally
            {
                Marshal.Release(pFactory);
            }
        }
        finally
        {
            HString.Delete(classNameHandle);
        }
    }
}
