Set-StrictMode -Version Latest

function Test-PublicCloudPublishableKey([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -match '(?i)service_role|sb_secret_|placeholder|change[-_ ]?me|example') {
        return $false
    }
    if ($Value.StartsWith('sb_publishable_', [StringComparison]::Ordinal)) {
        return $Value.Length -gt 31
    }

    $segments = $Value.Split('.')
    if ($segments.Count -ne 3) {
        return $false
    }
    try {
        $payload = $segments[1].Replace('-', '+').Replace('_', '/')
        $payload = $payload.PadRight(
            $payload.Length + ((4 - ($payload.Length % 4)) % 4),
            '=')
        $json = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($payload)) | ConvertFrom-Json
        return [string]$json.role -ceq 'anon'
    }
    catch {
        return $false
    }
}

function New-PublicCloudConfig(
    [string]$SupabaseUrl,
    [string]$PublishableKey,
    [string]$OrganizationId) {
    $parsedUrl = $null
    if ([string]::IsNullOrWhiteSpace($SupabaseUrl) -or
        -not [Uri]::TryCreate(
            $SupabaseUrl.Trim(),
            [UriKind]::Absolute,
            [ref]$parsedUrl) -or
        $parsedUrl.Scheme -cne 'https') {
        throw 'PUBLICCLOUD_INVALID_URL: official installer build requires EXAMTRANSFER_SUPABASE_URL with HTTPS.'
    }

    $normalizedPublishableKey = if ($null -eq $PublishableKey) {
        ''
    }
    else {
        $PublishableKey.Trim()
    }
    if (-not (Test-PublicCloudPublishableKey $normalizedPublishableKey)) {
        throw 'PUBLICCLOUD_INVALID_PUBLISHABLE_KEY: supply a publishable or legacy anon key; secret/service-role/placeholder values are rejected.'
    }

    $normalizedOrganizationId = if ($null -eq $OrganizationId) {
        ''
    }
    else {
        $OrganizationId.Trim()
    }
    $parsedOrganizationId = [guid]::Empty
    if ([string]::IsNullOrWhiteSpace($normalizedOrganizationId) -or
        -not [guid]::TryParse(
            $normalizedOrganizationId,
            [ref]$parsedOrganizationId) -or
        $parsedOrganizationId -eq [guid]::Empty) {
        throw 'PUBLICCLOUD_INVALID_ORGANIZATION_ID: EXAMTRANSFER_ORGANIZATION_ID must be a non-empty UUID, not a Supabase project ref or organization slug.'
    }

    return [pscustomobject][ordered]@{
        supabaseUrl = $parsedUrl.AbsoluteUri.TrimEnd('/')
        publishableKey = $normalizedPublishableKey
        organizationId = $parsedOrganizationId.ToString('D')
    }
}

function Read-PublicCloudConfig([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "PUBLICCLOUD_CONFIG_MISSING: $Path"
    }

    try {
        $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "PUBLICCLOUD_CONFIG_INVALID_JSON: $Path"
    }
    if ($null -eq $document -or $document -isnot [pscustomobject]) {
        throw "PUBLICCLOUD_CONFIG_INVALID_ROOT: $Path"
    }

    $required = @('supabaseUrl', 'publishableKey', 'organizationId')
    $properties = @($document.PSObject.Properties.Name)
    foreach ($name in $required) {
        if ($properties -cnotcontains $name -or
            [string]::IsNullOrWhiteSpace([string]$document.$name)) {
            throw "PUBLICCLOUD_CONFIG_REQUIRED_FIELD_MISSING: $name"
        }
    }
    foreach ($name in $properties) {
        if ($required -cnotcontains $name) {
            throw "PUBLICCLOUD_CONFIG_FORBIDDEN_FIELD: $name"
        }
    }

    return New-PublicCloudConfig `
        -SupabaseUrl ([string]$document.supabaseUrl) `
        -PublishableKey ([string]$document.publishableKey) `
        -OrganizationId ([string]$document.organizationId)
}

function Write-PublicCloudConfig(
    [string]$Path,
    [pscustomobject]$Config) {
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText(
        $Path,
        ($Config | ConvertTo-Json -Depth 2),
        (New-Object Text.UTF8Encoding($false)))
}

function Assert-PublicCloudConfigEqual(
    [pscustomobject]$Expected,
    [pscustomobject]$Actual,
    [string]$Stage) {
    foreach ($name in @('supabaseUrl', 'publishableKey', 'organizationId')) {
        if (-not [string]::Equals(
                [string]$Expected.$name,
                [string]$Actual.$name,
                [StringComparison]::Ordinal)) {
            throw "PUBLICCLOUD_CONFIG_ROUNDTRIP_MISMATCH: stage=$Stage field=$name"
        }
    }
}
