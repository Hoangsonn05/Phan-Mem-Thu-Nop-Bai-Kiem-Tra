param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherJwt,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$FileSessionId,
    [Parameter(Mandatory)][Guid]$QuizSessionId,
    [Parameter(Mandatory)][ValidateScript({Test-Path -LiteralPath $_ -PathType Leaf})][string]$ArchivePath
)
$ErrorActionPreference='Stop'
$scripts=@(
    @{Name='test-cloud-schema-version.ps1';Args=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,'-TeacherOrServiceJwt',$TeacherJwt)},
    @{Name='staging-publiccloud-e2e.ps1';Args=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,'-StudentJwt',$StudentJwt,'-SessionId',$FileSessionId)},
    @{Name='test-storage-workflow.ps1';Args=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,'-StudentJwt',$StudentJwt,'-SessionId',$FileSessionId,'-ArchivePath',$ArchivePath)},
    @{Name='test-device-command.ps1';Args=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,'-TeacherJwt',$TeacherJwt,'-StudentJwt',$StudentJwt,'-SessionId',$FileSessionId)},
    @{Name='test-quiz-workflow.ps1';Args=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,'-StudentJwt',$StudentJwt,'-SessionId',$QuizSessionId)}
)
foreach($entry in $scripts){ $callArgs=$entry.Args; & (Join-Path $PSScriptRoot $entry.Name) @callArgs; if($LASTEXITCODE -ne 0){throw "$($entry.Name) failed."} }
Write-Host 'PASS code=PUBLIC_CLOUD_STAGING_SUITE_OK detail=all live staging checks passed; sync roundtrip is a separate required gate because it needs LocalServer identifiers'
