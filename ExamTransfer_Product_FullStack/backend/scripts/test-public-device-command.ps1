param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherJwt,
    [Parameter(Mandatory)][string]$StudentJwt,
    [Parameter(Mandatory)][Guid]$SessionId,
    [string]$DeviceId="device-$([Guid]::NewGuid().ToString('N'))"
)
$arguments=@('-SupabaseUrl',$SupabaseUrl,'-PublishableKey',$PublishableKey,
    '-TeacherJwt',$TeacherJwt,'-StudentJwt',$StudentJwt,'-SessionId',$SessionId,'-DeviceId',$DeviceId)
& "$PSScriptRoot/test-device-command.ps1" @arguments
exit $LASTEXITCODE
