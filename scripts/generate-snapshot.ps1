param(
    [int]$Count = 100000,
    [string]$Out = "artifacts\bench-100k.txt"
)

$ErrorActionPreference = "Stop"

$folder = Split-Path -Parent $Out
if ($folder -and -not (Test-Path $folder)) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}

$roots = @(
    "C:\Users\Asus\Documents",
    "C:\Users\Asus\Downloads",
    "C:\Users\Asus\Pictures",
    "C:\Users\Asus\source\repos",
    "C:\Program Files",
    "D:\Projects"
)

$extensions = @(".txt", ".md", ".cs", ".json", ".png", ".pdf", ".xlsx", ".dll", ".exe", ".log")
$words = @("report", "notes", "invoice", "budget", "readme", "config", "backup", "design", "meeting", "draft", "final", "archive")

$builder = [System.Text.StringBuilder]::new()
$random = [System.Random]::new(12345)

for ($i = 0; $i -lt $Count; $i++) {
    $root = $roots[$i % $roots.Count]
    $folderIndex = $i % 50
    $word = $words[$random.Next($words.Count)]
    $extension = $extensions[$random.Next($extensions.Count)]
    $name = "$word-$($i.ToString('D6'))$extension"
    [void]$builder.AppendLine("$root\project$folderIndex\$name")
}

[System.IO.File]::WriteAllText((Join-Path (Get-Location) $Out), $builder.ToString())
Write-Output "Wrote $Count entries to $Out"
