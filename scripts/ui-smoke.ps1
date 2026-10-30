param(
    [string]$Exe = "artifacts\publish\glide.exe",
    [string]$AppArgs = "--snapshot artifacts\bench-100k.txt",
    [string]$Text = "report",
    [string]$Screenshot = "artifacts\ui-smoke.png",
    [string]$Hotkey = "Ctrl+Shift+Alt+/",
    [int]$ReadySeconds = 15
)

$modifierKeys = @{
    "ctrl" = 0x11; "control" = 0x11; "shift" = 0x10; "alt" = 0x12; "win" = 0x5B; "windows" = 0x5B
}

$namedKeys = @{
    "space" = 0x20; "enter" = 0x0D; "return" = 0x0D; "escape" = 0x1B; "esc" = 0x1B
    "tab" = 0x09; "backspace" = 0x08; "delete" = 0x2E; "insert" = 0x2D
    "home" = 0x24; "end" = 0x23; "pageup" = 0x21; "pagedown" = 0x22
    "up" = 0x26; "down" = 0x28; "left" = 0x25; "right" = 0x27
    "/" = 0xBF; "?" = 0xBF; ";" = 0xBA; "=" = 0xBB; "," = 0xBC; "-" = 0xBD; "." = 0xBE
    '`' = 0xC0; "[" = 0xDB; "\" = 0xDC; "]" = 0xDD
}

$parts = $Hotkey.Split("+") | ForEach-Object { $_.Trim() }
$mods = @()
foreach ($part in $parts[0..($parts.Count - 2)]) {
    if (-not $modifierKeys.ContainsKey($part.ToLowerInvariant())) {
        throw "Unknown modifier in -Hotkey: $part"
    }

    $mods += $modifierKeys[$part.ToLowerInvariant()]
}

$keyName = $parts[-1]
$keyVk = 0

if ($keyName.Length -eq 1 -and [char]::IsLetterOrDigit($keyName[0])) {
    $keyVk = [int][char]$keyName.ToUpperInvariant()
}
elseif ($keyName -match "^F([1-9]|1[0-2])$") {
    $keyVk = 0x70 + [int]$Matches[1] - 1
}
elseif ($namedKeys.ContainsKey($keyName.ToLowerInvariant())) {
    $keyVk = $namedKeys[$keyName.ToLowerInvariant()]
}
else {
    throw "Unknown key in -Hotkey: $keyName"
}

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

    public static void Hotkey(int[] modifiers, int key)
    {
        foreach (int modifier in modifiers)
        {
            keybd_event((byte)modifier, 0, 0, IntPtr.Zero);
        }

        keybd_event((byte)key, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        keybd_event((byte)key, 0, 2, IntPtr.Zero);

        for (int i = modifiers.Length - 1; i >= 0; i--)
        {
            keybd_event((byte)modifiers[i], 0, 2, IntPtr.Zero);
        }
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
Start-Sleep -Milliseconds 1200

$errorLog = Join-Path (Get-Location) "artifacts\ui-smoke.err"
$process = Start-Process -FilePath $Exe -ArgumentList $AppArgs -PassThru -RedirectStandardError $errorLog
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

# The window exists before RegisterHotKey has necessarily been called, and a cold
# start can take a second. Press once, then retry only while the window stays
# hidden, so a late press cannot toggle a window that is already up.
Start-Sleep -Milliseconds 600

for ($attempt = 1; $attempt -le 10; $attempt++) {
    [GlideProbe]::Hotkey($mods, $keyVk)
    Start-Sleep -Milliseconds 500

    if ([GlideProbe]::IsWindowVisible($hwnd)) {
        break
    }
}

Start-Sleep -Milliseconds 300
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

for ($attempt = 1; $attempt -le 5; $attempt++) {
    [GlideProbe]::Hotkey($mods, $keyVk)
    Start-Sleep -Milliseconds 500

    if ([GlideProbe]::IsWindowVisible($hwnd)) {
        break
    }
}

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
    if (Test-Path $errorLog) {
        Write-Output "--- stderr ---"
        Get-Content $errorLog | ForEach-Object { Write-Output $_ }
    }

    Write-Error ("UI smoke failed: " + ($failures -join ", "))
}

Write-Output "UI smoke passed"
