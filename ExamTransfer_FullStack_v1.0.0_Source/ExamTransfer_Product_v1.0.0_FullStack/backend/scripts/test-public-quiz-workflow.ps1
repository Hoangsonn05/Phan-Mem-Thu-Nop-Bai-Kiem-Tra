param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId
)
$arguments=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,
    '-StudentJwt',$StudentJwt,'-SessionId',$SessionId)
& "$PSScriptRoot/test-quiz-workflow.ps1" @arguments
exit $LASTEXITCODE
