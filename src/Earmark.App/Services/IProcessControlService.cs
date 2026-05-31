using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

/// <summary>Outcome of a close / terminate attempt against an external process.</summary>
public enum ProcessActionResult
{
    /// <summary>The request was issued (graceful close posted, or the process was killed).</summary>
    Success,

    /// <summary>The process has no main window to send a graceful close to. Caller can suggest
    /// force-terminate instead.</summary>
    NoWindow,

    /// <summary>We can't act on the process: it runs at a higher integrity level (elevated) or as
    /// another user, and Earmark isn't elevated enough to reach it.</summary>
    AccessDenied,

    /// <summary>The process is no longer running.</summary>
    NotFound,

    /// <summary>An unexpected failure (see the log).</summary>
    Failed,
}

/// <summary>Closes or force-terminates an external process by pid, and pre-detects whether we're
/// allowed to. Keeps the Win32 probe and <see cref="Process"/> calls out of the view-models.</summary>
public interface IProcessControlService
{
    /// <summary>True when we can force-terminate this process: probes for <c>PROCESS_TERMINATE</c>
    /// access. Drives the Terminate item. Note this is a DIFFERENT permission from a graceful close -
    /// on some setups <c>PROCESS_TERMINATE</c> succeeds against an elevated target even though a
    /// <c>WM_CLOSE</c> to its window is UIPI-blocked (see <see cref="CanClose"/>).</summary>
    bool CanControl(uint pid);

    /// <summary>True when a graceful <c>WM_CLOSE</c> would reach this process's windows: its integrity
    /// level is at or below Earmark's. UI Privilege Isolation silently drops window messages sent to a
    /// HIGHER-integrity window, so an elevated target is un-closeable from a medium Earmark even when
    /// <see cref="CanControl"/> (terminate) is true. Drives the Close item's enabled state.</summary>
    bool CanClose(uint pid);

    /// <summary>True when the process runs at High integrity or above - i.e. it's running elevated / as
    /// administrator. Drives the shield badge on the chip (absolute, independent of Earmark's own
    /// elevation), matching the Windows UAC-shield convention.</summary>
    bool IsElevated(uint pid);

    /// <summary>Politely asks the process to close (posts <c>WM_CLOSE</c> to its main window), giving
    /// it the chance to run its own shutdown / save-prompt path. The Windows equivalent of SIGTERM.</summary>
    ProcessActionResult Close(uint pid);

    /// <summary>Force-terminates the process (<c>TerminateProcess</c>) with no chance to save. The
    /// Windows equivalent of SIGKILL.</summary>
    ProcessActionResult Kill(uint pid);
}

internal sealed class ProcessControlService : IProcessControlService
{
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int SECURITY_MANDATORY_HIGH_RID = 0x3000;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private readonly ILogger<ProcessControlService> _logger;

    public ProcessControlService(ILogger<ProcessControlService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool CanControl(uint pid) => Probe(pid) == ProcessActionResult.Success;

    public bool CanClose(uint pid)
    {
        var own = OwnIntegrity();
        var target = TargetIntegrityRid(pid);
        // Couldn't read one side's integrity - don't over-disable; the runtime toast covers a real block.
        if (own < 0 || target < 0) return true;
        return target <= own;   // WM_CLOSE only reaches same-or-lower integrity windows
    }

    public bool IsElevated(uint pid) => TargetIntegrityRid(pid) >= SECURITY_MANDATORY_HIGH_RID;

    private int? _ownIntegrity;

    /// <summary>Earmark's own integrity RID, cached - a running process's integrity never changes.</summary>
    private int OwnIntegrity() => _ownIntegrity ??= IntegrityRid(GetCurrentProcess());

    /// <summary>Reads a target process's integrity RID via a QUERY_LIMITED handle, or -1 if it can't be
    /// opened or read. QUERY_LIMITED is granted across integrity levels, so this works against an
    /// elevated target from a medium Earmark.</summary>
    private static int TargetIntegrityRid(uint pid)
    {
        if (pid == 0) return -1;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (handle == IntPtr.Zero) return -1;
        try { return IntegrityRid(handle); }
        finally { CloseHandle(handle); }
    }

    /// <summary>Reads a process's mandatory integrity RID (0x2000 medium, 0x3000 high, 0x4000 system,
    /// etc.) from its token, or -1 on failure. The token handle is opened and closed here; the process
    /// handle is the caller's to manage (GetCurrentProcess returns a pseudo-handle that mustn't be
    /// closed, so it's never closed here).</summary>
    private static int IntegrityRid(IntPtr processHandle)
    {
        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var token)) return -1;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0) return -1;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return -1;
                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; } - Label.Sid is the leading pointer.
                var sid = Marshal.ReadIntPtr(buffer);
                var subAuthorityCount = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    public ProcessActionResult Close(uint pid)
    {
        var probe = Probe(pid);
        if (probe != ProcessActionResult.Success) return probe;   // AccessDenied / NotFound

        // Post WM_CLOSE to the process's own top-level, un-owned, visible windows - the same message
        // the title-bar X sends, so the app runs its save / shutdown path. Enumerating the windows
        // ourselves is far more reliable than Process.CloseMainWindow(): its MainWindowHandle heuristic
        // returns 0 for many real apps (wxWidgets like Audacity, some Qt / Electron shells), which made
        // a perfectly closeable app report "no window". Owned windows (dialogs, palettes) are skipped so
        // we hit the main frame, not a tool window.
        var windows = new List<IntPtr>();
        bool Collect(IntPtr hWnd, IntPtr _)
        {
            if (GetWindowThreadProcessId(hWnd, out var windowPid) != 0
                && windowPid == pid
                && IsWindowVisible(hWnd)
                && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
            {
                windows.Add(hWnd);
            }
            return true;
        }
        EnumWindows(Collect, IntPtr.Zero);

        if (windows.Count == 0) return ProcessActionResult.NoWindow;

        var posted = false;
        foreach (var hWnd in windows)
        {
            if (PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) posted = true;
        }
        if (posted) return ProcessActionResult.Success;

        // Windows existed but every post was refused - UIPI blocking a WM_CLOSE to a window running at
        // a higher UI privilege than Earmark's thread. TerminateProcess takes a different access path,
        // so killing can still work where a graceful close can't; the caller steers the user there.
        // Capture the error BEFORE logging: the file logger flushes per call, and that write resets the
        // thread's last-error, so a second GetLastWin32Error() would read the log's syscall, not ours.
        var lastError = Marshal.GetLastWin32Error();
        _logger.LogWarning("Close refused for pid {Pid} (lastError={Err})", pid, lastError);
        return lastError == ERROR_ACCESS_DENIED
            ? ProcessActionResult.AccessDenied
            : ProcessActionResult.Failed;
    }

    public ProcessActionResult Kill(uint pid)
    {
        var probe = Probe(pid);
        if (probe != ProcessActionResult.Success) return probe;   // AccessDenied / NotFound

        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.Kill();
            return ProcessActionResult.Success;
        }
        catch (ArgumentException) { return ProcessActionResult.NotFound; }
        catch (InvalidOperationException) { return ProcessActionResult.NotFound; }
        catch (Win32Exception) { return ProcessActionResult.AccessDenied; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kill failed for pid {Pid}", pid);
            return ProcessActionResult.Failed;
        }
    }

    /// <summary>Probes for terminate access without side effects: opening a handle and closing it
    /// immediately does nothing to the target. Returns <see cref="ProcessActionResult.Success"/> when
    /// reachable, <see cref="ProcessActionResult.AccessDenied"/> for an elevated / cross-user target,
    /// or <see cref="ProcessActionResult.NotFound"/> when the pid isn't a live process.</summary>
    private static ProcessActionResult Probe(uint pid)
    {
        if (pid == 0) return ProcessActionResult.NotFound;

        var handle = OpenProcess(PROCESS_TERMINATE, false, (int)pid);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
            return ProcessActionResult.Success;
        }

        return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED
            ? ProcessActionResult.AccessDenied
            : ProcessActionResult.NotFound;
    }
}
