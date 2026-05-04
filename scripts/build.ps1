param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "artifacts\ServerUpdateRepairTool"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "WinUpdateRepairTool.csproj"
$outputPath = Join-Path $repoRoot $Output

dotnet restore $project
dotnet build $project -c $Configuration --no-restore
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $outputPath

Write-Host ""
Write-Host "Published executable:"
Write-Host (Join-Path $outputPath "ServerUpdateRepairTool.exe")
