$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "..\artifacts\win-x64"
dotnet publish "$PSScriptRoot\..\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $out
Write-Host "Published to $out"
