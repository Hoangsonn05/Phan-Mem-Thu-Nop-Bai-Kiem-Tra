param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$frontendRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $frontendRoot 'src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'

Get-ChildItem -Path $frontendRoot -Directory -Recurse |
    Where-Object { $_.Name -in @('bin', 'obj') } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw 'Frontend restore failed.' }

dotnet build $project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }

Write-Host 'Frontend build succeeded.' -ForegroundColor Green
