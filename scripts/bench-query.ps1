param(
    [string]$Exe = "artifacts\publish\glide.exe",
    [string]$Snapshot = "artifacts\bench-100k.txt",
    [string]$Query = "report",
    [int]$Iterations = 50,
    [int]$BudgetMs = 30
)

$ErrorActionPreference = "Stop"

& $Exe --bench --snapshot $Snapshot --query $Query --iterations $Iterations --budget-ms $BudgetMs
exit $LASTEXITCODE
