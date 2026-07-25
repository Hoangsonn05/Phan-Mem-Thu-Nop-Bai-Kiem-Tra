[CmdletBinding()]
param(
    [string]$BackendRoot,
    [Parameter(Mandatory)]
    [string]$ProjectRef,
    [switch]$IncludeSeed,
    [switch]$RunDatabaseTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

throw @"
This legacy remote-write script is intentionally disabled.

It could link and push a Supabase project without a verified database/Storage
backup or a completed production-readiness report. Do not re-enable it.

Use these guarded scripts instead:
1. check-production-update-readiness.ps1
2. backup-supabase-production-all.ps1
3. verify-supabase-production-backup.ps1
4. check-production-update-readiness.ps1 again with the backup and remote dry-run
5. apply-supabase-production-update.ps1

Requested Project Ref was: $ProjectRef
"@
