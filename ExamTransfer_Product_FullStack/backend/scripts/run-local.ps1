$ErrorActionPreference = "Stop"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet restore "$PSScriptRoot\..\ExamTransfer.sln"
dotnet run --project "$PSScriptRoot\..\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj"
