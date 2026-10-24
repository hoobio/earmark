using System.Runtime.InteropServices;

namespace Earmark.Audio.Services;

public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged { get; } = ResolveIsPackaged();

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    private static bool ResolveIsPackaged()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }
}
