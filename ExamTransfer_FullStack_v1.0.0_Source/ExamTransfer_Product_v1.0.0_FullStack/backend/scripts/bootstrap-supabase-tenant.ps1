#Requires -Version 5.1
<#
.SYNOPSIS
    Creates the first organization and Admin profile for an ExamTransfer Supabase project.

.DESCRIPTION
    Logs in to Supabase Auth (email/password or provided access token), then calls
    RPC public.bootstrap_examtransfer_organization(organization_name, display_name)
    to create the organization and Admin profile.

    Running again when a profile already exists returns the existing organization_id
    (idempotent since migration 20260718025505_bootstrap_idempotent).

.PARAMETER SupabaseUrl
    Root URL of the Supabase project, e.g. https://xxxx.supabase.co

.PARAMETER PublishableKey
    Anon/publishable key of the project.

.PARAMETER AccessToken
    JWT access token for an already-authenticated user.

.PARAMETER Email
    Email address for Supabase Auth login.

.PARAMETER Password
    SecureString password. Plain text only lives in memory for the minimum duration.

.PARAMETER OrganizationName
    Organization name (supports Unicode / Vietnamese accented characters, min 2 chars).

.PARAMETER DisplayName
    Display name for the Admin profile (defaults to "Administrator").

.PARAMETER DryRunRequest
    When set, only creates and validates JSON payloads without sending real requests.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SupabaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$PublishableKey,

    [Parameter(Mandatory = $true, ParameterSetName = 'Token')]
    [string]$AccessToken,

    [Parameter(Mandatory = $true, ParameterSetName = 'Credentials')]
    [string]$Email,

    [Parameter(Mandatory = $true, ParameterSetName = 'Credentials')]
    [Security.SecureString]$Password,

    [Parameter(Mandatory = $true)]
    [string]$OrganizationName,

    [string]$DisplayName = 'Administrator',

    [switch]$DryRunRequest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helper: Serialize hashtable/object to a validated JSON STRING.
#
# ROOT CAUSE FIX: The previous version returned byte[], which PowerShell 5.1
# enumerates through the pipeline, turning byte[] into a sequence of integers
# (e.g. "123\n34\n101\n..."). Invoke-RestMethod then sent that integer sequence
# as the body, causing Supabase Auth to report:
#   "invalid character '3' after top-level value"
# because the body started with "123" (byte value of '{') instead of '{'.
#
# This helper returns [string] so Invoke-RestMethod always receives valid JSON text.
# NEVER logs password, access_token, refresh_token or any key material.
# ---------------------------------------------------------------------------
function ConvertTo-JsonRequestBody {
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,

        [string]$Description = 'Request'
    )

    $json = $InputObject | ConvertTo-Json -Depth 16 -Compress

    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "$Description JSON body is empty after serialization."
    }

    # Round-trip: confirm JSON can be parsed back
    try {
        $null = $json | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$Description JSON body failed round-trip validation: $($_.Exception.Message)"
    }

    # Guard: must be a plain string, not a hashtable or array string
    if ($json -like '*System.Collections*') {
        throw "$Description JSON body contains unconverted PowerShell objects."
    }

    return [string]$json
}

# ---------------------------------------------------------------------------
# Helper: POST request with JSON string body and full headers.
# Safe diagnostics on failure: endpoint, key names, string length, HTTP status,
# response body. Never shows password or token values.
# ---------------------------------------------------------------------------
function Invoke-JsonPost {
    [OutputType([object])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers,

        # Body MUST be [string] - passing byte[] to Invoke-RestMethod on PS 5.1
        # causes enumeration of byte values as individual integers.
        [Parameter(Mandatory = $true)]
        [string]$Body,

        [string]$Label = $Uri
    )

    if ($DryRunRequest) {
        $safeKeys = ($Headers.Keys | Where-Object { $_ -ne 'Authorization' }) -join ', '
        Write-Host "[DRY-RUN] $Label" -ForegroundColor Yellow
        Write-Host "  Endpoint    : $Uri"
        Write-Host "  Header keys : $safeKeys"
        Write-Host "  Body type   : $($Body.GetType().FullName)"
        Write-Host "  Body length : $($Body.Length) chars"
        return $null
    }

    try {
        return Invoke-RestMethod `
            -Method      Post `
            -Uri         $Uri `
            -Headers     $Headers `
            -ContentType 'application/json; charset=utf-8' `
            -Body        $Body
    }
    catch {
        $statusCode   = $null
        $responseBody = $null
        $exResp       = $null

        if ($_.Exception -is [System.Net.WebException]) {
            $exResp = ([System.Net.WebException]$_.Exception).Response
        }
        elseif ($null -ne $_.Exception.Response) {
            $exResp = $_.Exception.Response
        }

        if ($null -ne $exResp) {
            try {
                $statusCode = [int]$exResp.StatusCode
                $stream     = $exResp.GetResponseStream()
                $reader     = [System.IO.StreamReader]::new(
                    $stream, [System.Text.Encoding]::UTF8)
                $responseBody = $reader.ReadToEnd()
                $reader.Dispose()
            }
            catch { }
        }

        # Safe: show key names and char count, never show token values
        $parsedKeys = ''
        try {
            $parsedObj  = $Body | ConvertFrom-Json
            $parsedKeys = ($parsedObj.PSObject.Properties.Name) -join ', '
        }
        catch {
            $parsedKeys = '(could not parse)'
        }

        $utf8Len = [System.Text.UTF8Encoding]::new($false).GetByteCount($Body)

        throw @"
Request failed: $Label
  Endpoint    : $Uri
  JSON keys   : $parsedKeys
  Body type   : $($Body.GetType().FullName)
  Body chars  : $($Body.Length)
  UTF-8 bytes : $utf8Len
  HTTP status : $statusCode
  Response    : $responseBody
  Detail      : $($_.Exception.Message)
"@
    }
}

# ---------------------------------------------------------------------------
# Input validation
# ---------------------------------------------------------------------------
$baseUrl = $SupabaseUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($baseUrl)) {
    throw 'SupabaseUrl is required.'
}
if ([string]::IsNullOrWhiteSpace($PublishableKey)) {
    throw 'PublishableKey is required.'
}
if ([string]::IsNullOrWhiteSpace($OrganizationName) -or
    $OrganizationName.Trim().Length -lt 2) {
    throw 'OrganizationName must be at least 2 characters.'
}

# ---------------------------------------------------------------------------
# Step 1: Supabase Auth login (Credentials parameter set only)
# ---------------------------------------------------------------------------
if ($PSCmdlet.ParameterSetName -eq 'Credentials') {
    $plainPassword = $null
    try {
        $plainPassword = [System.Net.NetworkCredential]::new('', $Password).Password

        $authPayload = [ordered]@{
            email    = $Email
            password = $plainPassword
        }

        # ConvertTo-JsonRequestBody returns [string] - safe for Invoke-RestMethod
        [string]$authBody = ConvertTo-JsonRequestBody `
            -InputObject $authPayload `
            -Description 'Supabase Auth login'

        if ($DryRunRequest) {
            Write-Host '[DRY-RUN] Auth login request' -ForegroundColor Yellow
            Write-Host "  Endpoint    : $baseUrl/auth/v1/token?grant_type=password"
            Write-Host '  JSON keys   : email, password'
            Write-Host "  Body type   : $($authBody.GetType().FullName)"
            Write-Host "  Body length : $($authBody.Length) chars"
            Write-Host '  (password value not shown)'
            $AccessToken = 'DRY_RUN_TOKEN'
        }
        else {
            Write-Host 'Logging in to Supabase Auth...' -ForegroundColor Cyan

            $login = $null
            try {
                $login = Invoke-RestMethod `
                    -Method      Post `
                    -Uri         "$baseUrl/auth/v1/token?grant_type=password" `
                    -Headers     @{ apikey = $PublishableKey; Accept = 'application/json' } `
                    -ContentType 'application/json; charset=utf-8' `
                    -Body        $authBody
            }
            catch {
                $statusCode   = $null
                $responseBody = $null
                $exResp       = $null

                if ($_.Exception -is [System.Net.WebException]) {
                    $exResp = ([System.Net.WebException]$_.Exception).Response
                }
                elseif ($null -ne $_.Exception.Response) {
                    $exResp = $_.Exception.Response
                }

                if ($null -ne $exResp) {
                    try {
                        $statusCode = [int]$exResp.StatusCode
                        $stream     = $exResp.GetResponseStream()
                        $reader     = [System.IO.StreamReader]::new(
                            $stream, [System.Text.Encoding]::UTF8)
                        $responseBody = $reader.ReadToEnd()
                        $reader.Dispose()
                    }
                    catch { }
                }

                # Safe diagnostics - never print password value
                $utf8Len = [System.Text.UTF8Encoding]::new($false).GetByteCount($authBody)
                throw @"
Auth login failed.
  Endpoint    : /auth/v1/token?grant_type=password
  JSON keys   : email, password
  Body type   : $($authBody.GetType().FullName)
  UTF-8 bytes : $utf8Len
  HTTP status : $statusCode
  Response    : $responseBody
  Detail      : $($_.Exception.Message)
"@
            }
            finally {
                # authBody may contain plain password in JSON - clear it
                Remove-Variable -Name authBody -ErrorAction SilentlyContinue
            }

            # Verify required response fields - never print token values
            if ($null -eq $login -or
                [string]::IsNullOrWhiteSpace($login.access_token)) {
                throw 'Supabase Auth response did not contain access_token.'
            }
            if ([string]::IsNullOrWhiteSpace($login.refresh_token)) {
                throw 'Supabase Auth response did not contain refresh_token.'
            }
            if ($null -eq $login.user -or
                [string]::IsNullOrWhiteSpace($login.user.id)) {
                throw 'Supabase Auth response did not contain user.id.'
            }

            $AccessToken = [string]$login.access_token
            Write-Host "Authenticated successfully. User ID: $($login.user.id)" `
                -ForegroundColor Green
        }
    }
    finally {
        # Clear plain-text password from memory
        $plainPassword = $null
        # Clear auth payload (contains password)
        Remove-Variable -Name authPayload -ErrorAction SilentlyContinue
    }
}

if ([string]::IsNullOrWhiteSpace($AccessToken) -and -not $DryRunRequest) {
    throw 'No access token available. Provide -AccessToken or -Email/-Password.'
}

# ---------------------------------------------------------------------------
# Step 2: Call RPC bootstrap_examtransfer_organization
#
# Function signature (migration 202607150004 + 20260718025505_bootstrap_idempotent):
#   public.bootstrap_examtransfer_organization(
#     organization_name text,    -- parameter name, no prefix
#     display_name      text default ''
#   ) returns uuid
#
# JSON body keys MUST match PostgreSQL parameter names exactly.
# Authorization MUST use the user's access token, NOT the publishable key.
# ---------------------------------------------------------------------------
Write-Host 'Creating or resolving organization...' -ForegroundColor Cyan

$rpcPayload = [ordered]@{
    organization_name = $OrganizationName.Trim()
    display_name      = $(
        if ([string]::IsNullOrWhiteSpace($DisplayName)) { 'Administrator' }
        else { $DisplayName.Trim() }
    )
}

# ConvertTo-JsonRequestBody returns [string] - safe for Invoke-RestMethod
[string]$rpcBody = ConvertTo-JsonRequestBody `
    -InputObject $rpcPayload `
    -Description 'Bootstrap RPC'

# DryRun: validate and print summary without sending
if ($DryRunRequest) {
    $roundTrip = $rpcBody | ConvertFrom-Json
    $nameMatch = ($roundTrip.organization_name -eq $OrganizationName.Trim())

    $utf8Len = [System.Text.UTF8Encoding]::new($false).GetByteCount($rpcBody)

    Write-Host ''
    Write-Host '=== DRY-RUN REPORT ===' -ForegroundColor Yellow
    Write-Host "  RPC endpoint      : $baseUrl/rest/v1/rpc/bootstrap_examtransfer_organization"
    Write-Host "  JSON keys         : $($rpcPayload.Keys -join ', ')"
    Write-Host "  Body type         : $($rpcBody.GetType().FullName)"
    Write-Host "  Body length       : $($rpcBody.Length) chars"
    Write-Host "  UTF-8 bytes       : $utf8Len"
    Write-Host "  OrganizationName round-trip OK: $nameMatch"
    if (-not $nameMatch) {
        Write-Warning "OrganizationName mismatch! Original: '$($OrganizationName.Trim())' | Parsed: '$($roundTrip.organization_name)'"
    }
    Write-Host '  Authorization     : Bearer <user access token> (value not shown)'
    Write-Host '=== END DRY-RUN ===' -ForegroundColor Yellow
    return
}

$rpcHeaders = @{
    apikey        = $PublishableKey
    Authorization = "Bearer $AccessToken"
    Accept        = 'application/json'
}

$rpcEndpoint = "$baseUrl/rest/v1/rpc/bootstrap_examtransfer_organization"

$result = Invoke-JsonPost `
    -Uri     $rpcEndpoint `
    -Headers $rpcHeaders `
    -Body    $rpcBody `
    -Label   'bootstrap_examtransfer_organization'

# ---------------------------------------------------------------------------
# Step 3: Output result
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Bootstrap succeeded.' -ForegroundColor Green
Write-Host "OrganizationId : $result"
Write-Host "Profile role   : Admin"
Write-Host ''
Write-Host 'Use this UUID as Cloud:OrganizationId in configure-supabase.ps1:' `
    -ForegroundColor Cyan
Write-Host ''
Write-Host "    .\backend\scripts\configure-supabase.ps1 ``"
Write-Host "        -SupabaseUrl   `"$SupabaseUrl`" ``"
Write-Host "        -PublishableKey `"<publishable_key>`" ``"
Write-Host "        -OrganizationId `"$result`""
Write-Host ''
