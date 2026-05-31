#Requires -Version 7
<#
.SYNOPSIS
    Captures a PNG screenshot of the running Earmark.App main window.
.DESCRIPTION
    Finds the Earmark.App process, restores + foregrounds its main window, waits briefly for
    the compositor to settle, then captures the window's DWM extended-frame bounds via
    Graphics.CopyFromScreen. A dev/QA aid for before/after UI comparison; not shipped.
.PARAMETER OutPath
    Destination PNG path. Parent directory is created if missing.
.PARAMETER DelayMs
    Settle delay after foregrounding (default 800ms).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutPath,
    [int] $DelayMs = 800
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$nativeMethods = @'
using System;
using System.Runtime.InteropServices;
public static class Win32Capture {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // Forces a window to the foreground from a background process, defeating Windows' foreground
    // lock: an ALT key tap satisfies the "recent user input" rule, and attaching to the current
    // foreground thread's input queue lets SetForegroundWindow take effect. This matters because GDI
    // screen capture of a WinUI 3 window returns black unless it is the foreground window (its
    // swapchain is otherwise promoted to a hardware overlay plane GDI can't read).
    public static void ForceForeground(IntPtr hWnd) {
        const byte VK_MENU = 0x12; const uint KEYUP = 0x2;
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint cur = GetCurrentThreadId();
        if (fg != cur) AttachThreadInput(cur, fg, true);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        if (fg != cur) AttachThreadInput(cur, fg, false);
    }
}
'@
if (-not ([System.Management.Automation.PSTypeName]'Win32Capture').Type) {
    Add-Type -TypeDefinition $nativeMethods
}

$proc = Get-Process -Name 'Earmark.App' -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Error 'Earmark.App is not running (or has no main window).'; exit 1 }

$hwnd = $proc.MainWindowHandle
if ([Win32Capture]::IsIconic($hwnd)) { [void][Win32Capture]::ShowWindow($hwnd, 9) }  # SW_RESTORE
[Win32Capture]::ForceForeground($hwnd)
Start-Sleep -Milliseconds $DelayMs

# DWMWA_EXTENDED_FRAME_BOUNDS = 9 - the true visible window rect (excludes the invisible resize border).
$rect = New-Object Win32Capture+RECT
$hr = [Win32Capture]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, [System.Runtime.InteropServices.Marshal]::SizeOf($rect))
if ($hr -ne 0) { [void][Win32Capture]::GetWindowRect($hwnd, [ref]$rect) }

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { Write-Error "Bad window rect ($width x $height)."; exit 1 }

$dir = Split-Path -Parent $OutPath
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$bmp = New-Object System.Drawing.Bitmap $width, $height
try {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))
    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
} finally { $bmp.Dispose() }

Write-Output "Captured ${width}x${height} -> $OutPath"
