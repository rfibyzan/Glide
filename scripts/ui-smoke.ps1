param(
    [string]$Exe = "artifacts\publish\glide.exe",
    [string]$AppArgs = "--snapshot artifacts\bench-100k.txt",
    [string]$Text = "report",
    [string]$Screenshot = "artifacts\ui-smoke.png",
    [int]$ReadySeconds = 15
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class GlideProbe
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    public static IntPtr FindByProcess(uint target)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target)
            {
                result = h;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static void Hotkey()
    {
        keybd_event(0x11, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x20, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        keybd_event(0x20, 0, 2, IntPtr.Zero);
        keybd_event(0x12, 0, 2, IntPtr.Zero);
        keybd_event(0x11, 0, 2, IntPtr.Zero);
    }

    public static void Type(IntPtr hWnd, string text)
    {
        foreach (char c in text)
        {
            PostMessageW(hWnd, 0x0102, (IntPtr)c, IntPtr.Zero);
        }
    }

    public static void Key(IntPtr hWnd, int virtualKey)
    {
        PostMessageW(hWnd, 0x0100, (IntPtr)virtualKey, IntPtr.Zero);
    }

    public static void Deactivate(IntPtr hWnd)
    {
        PostMessageW(hWnd, 0x0006, IntPtr.Zero, IntPtr.Zero);
    }
}
'@

$failures = New-Object System.Collections.Generic.List[string]

function Check([string]$Name, [bool]$Condition) {
    if ($Condition) {
        Write-Output "  ok   $Name"
    }
    else {
        Write-Output "  FAIL $Name"
        $failures.Add($Name)
    }
}

if (-not (Test-Path $Exe)) {
    throw "Executable not found: $Exe"
}

Get-Process glide -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$process = Start-Process -FilePath $Exe -ArgumentList $AppArgs -PassThru
$deadline = (Get-Date).AddSeconds($ReadySeconds)
$hwnd = [IntPtr]::Zero

while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
    $hwnd = [GlideProbe]::FindByProcess([uint32]$process.Id)
    if ($hwnd -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 250
    }
}

if ($hwnd -eq [IntPtr]::Zero) {
    throw "No window appeared for process $($process.Id)"
}

Write-Output "window: $($hwnd.ToInt64())"

[GlideProbe]::Hotkey()
Start-Sleep -Milliseconds 800
Check "hotkey shows the window" ([GlideProbe]::IsWindowVisible($hwnd))
Check "hotkey takes focus" ([GlideProbe]::GetForegroundWindow() -eq $hwnd)

[GlideProbe]::Type($hwnd, $Text)
Start-Sleep -Milliseconds 600
Check "typing keeps the window open" ([GlideProbe]::IsWindowVisible($hwnd))

[GlideProbe]::Key($hwnd, 0x28)
Start-Sleep -Milliseconds 200

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bitmap.Save((Join-Path (Get-Location) $Screenshot), [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()

Start-Sleep -Milliseconds 300
[GlideProbe]::Deactivate($hwnd)
Start-Sleep -Milliseconds 500
Check "losing focus hides the window" (-not [GlideProbe]::IsWindowVisible($hwnd))

[GlideProbe]::Hotkey()
Start-Sleep -Milliseconds 700
Check "hotkey reopens the window" ([GlideProbe]::IsWindowVisible($hwnd))

[GlideProbe]::Key($hwnd, 0x1B)
Start-Sleep -Milliseconds 400
Check "Escape hides the window" (-not [GlideProbe]::IsWindowVisible($hwnd))

$process.Refresh()
$workingSet = [math]::Round($process.WorkingSet64 / 1MB, 1)
Write-Output ("working set: {0} MB" -f $workingSet)

if (-not $process.HasExited) {
    $process.Kill()
    $process.WaitForExit()
}

if ($failures.Count -gt 0) {
    Write-Error ("UI smoke failed: " + ($failures -join ", "))
}

Write-Output "UI smoke passed"
