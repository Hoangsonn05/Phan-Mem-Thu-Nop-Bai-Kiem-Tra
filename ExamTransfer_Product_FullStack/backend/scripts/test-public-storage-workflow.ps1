param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId,
    [Parameter(Mandatory)][string]$ArchivePath
)
$arguments=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,
    '-StudentJwt',$StudentJwt,'-SessionId',$SessionId,'-ArchivePath',$ArchivePath)
& "$PSScriptRoot/test-storage-workflow.ps1" @arguments
exit $LASTEXITCODE
