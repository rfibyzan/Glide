param(
    [string]$Exe = "artifacts\publish\glide.exe",
    [int]$BudgetMB = 50,
    [int]$Seconds = 5,
    [int]$SampleMs = 200
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Exe)) {
    Write-Error "Executable not found: $Exe. Publish first: dotnet publish src\Glide.App -c Release -r win-x64 -o artifacts\publish"
}

$process = Start-Process -FilePath $Exe -ArgumentList "--idle", "$Seconds" -PassThru
$samples = New-Object System.Collections.Generic.List[double]

try {
    while (-not $process.HasExited) {
        $process.Refresh()
        $samples.Add($process.WorkingSet64 / 1MB)
        Start-Sleep -Milliseconds $SampleMs
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}

if ($samples.Count -eq 0) {
    Write-Error "No samples collected from $Exe"
}

$sorted = $samples | Sort-Object
$p95Index = [math]::Min($sorted.Count - 1, [math]::Ceiling($sorted.Count * 0.95) - 1)

$result = [pscustomobject]@{
    Exe      = $Exe
    Samples  = $samples.Count
    AverageMB = [math]::Round(($samples | Measure-Object -Average).Average, 1)
    P95MB    = [math]::Round($sorted[$p95Index], 1)
    MaxMB    = [math]::Round($sorted[-1], 1)
    BudgetMB = $BudgetMB
}

$result | Format-List

if ($result.P95MB -gt $BudgetMB) {
    Write-Error "Memory gate FAILED: p95 $($result.P95MB) MB exceeds budget $BudgetMB MB"
}

Write-Output "Memory gate passed: p95 $($result.P95MB) MB within $BudgetMB MB"
