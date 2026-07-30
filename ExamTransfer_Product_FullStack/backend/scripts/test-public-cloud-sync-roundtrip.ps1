param(
    [Parameter(Mandatory)][string]$LocalServerUrl,
    [Parameter(Mandatory)][string]$TeacherToken,
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherJwt,
    [Parameter(Mandatory)][Guid]$LocalSessionId,
    [Parameter(Mandatory)][string]$CloudEntityName,
    [Parameter(Mandatory)][string]$CloudEntityId
)
$arguments=@('-LocalServerUrl',$LocalServerUrl,'-TeacherToken',$TeacherToken,
    '-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,'-TeacherJwt',$TeacherJwt,
    '-LocalSessionId',$LocalSessionId,'-CloudEntityName',$CloudEntityName,'-CloudEntityId',$CloudEntityId)
& "$PSScriptRoot/test-sync-roundtrip.ps1" @arguments
exit $LASTEXITCODE
