# Run with PowerShell 7 against a caller-owned, isolated API with Database = null.
# The two successful uploads remain in that isolated API's uploads directory.
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [string]$Evidence,
    [string]$UploadDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$baseUri = [Uri]$BaseUrl
$address = $null
if ($baseUri.Scheme -ne 'http' -or
    -not [System.Net.IPAddress]::TryParse($baseUri.DnsSafeHost, [ref]$address) -or
    -not [System.Net.IPAddress]::IsLoopback($address) -or
    $baseUri.AbsolutePath -ne '/' -or $baseUri.Query -or $baseUri.Fragment) {
    throw 'BaseUrl must be an HTTP loopback IP origin for an isolated API (for example http://127.0.0.1:5081).'
}
$BaseUrl = $baseUri.GetLeftPart([UriPartial]::Authority)
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$handler.UseProxy = $false
$http = [System.Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(10)
$checks = [System.Collections.Generic.List[object]]::new()
$png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
$pngHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($png))

function Get-UploadSnapshot {
    if (-not $UploadDirectory) { return $null }
    if (-not (Test-Path -LiteralPath $UploadDirectory -PathType Container)) { return '[]' }
    $files = @(Get-ChildItem -LiteralPath $UploadDirectory -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = $_.Name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    return ConvertTo-Json -InputObject $files -Depth 3 -Compress
}

function Invoke-UploadRequest {
    param(
        [byte[]]$Bytes = $png,
        [string]$FileName = 'test.png',
        [string]$FieldName = 'file',
        [switch]$MissingFile,
        [switch]$WrongContentType,
        [switch]$InvalidBearer
    )
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl/api/media/upload")
    $response = $null
    try {
        if ($WrongContentType) {
            $request.Content = [System.Net.Http.StringContent]::new('{}', [Text.Encoding]::UTF8, 'application/json')
        } else {
            $multipart = [System.Net.Http.MultipartFormDataContent]::new()
            $request.Content = $multipart
            if (-not $MissingFile) {
                $file = [System.Net.Http.ByteArrayContent]::new($Bytes)
                $file.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('image/png')
                $multipart.Add($file, $FieldName, $FileName)
            }
        }
        if ($InvalidBearer) {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'media-upload-smoke-invalid-session')
        }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $json = $null
        if ($body) {
            try { $json = ConvertFrom-Json -InputObject $body -AsHashtable } catch {}
        }
        return [pscustomobject]@{ status = [int]$response.StatusCode; json = $json }
    } finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }
}

function Test-UploadCase {
    param([string]$Name, [scriptblock]$Check)
    try {
        $detail = & $Check
        $checks.Add([ordered]@{ name = $Name; passed = $true; detail = $detail })
    } catch {
        $checks.Add([ordered]@{ name = $Name; passed = $false; error = $_.Exception.Message })
    }
}

function Assert-UploadedImage {
    param($Result)
    if ($Result.status -ne 200) { throw "Expected multipart upload HTTP 200 without an antiforgery token; received HTTP $($Result.status)." }
    if ($null -eq $Result.json -or $Result.json.success -ne $true -or -not $Result.json.url) {
        throw 'Successful upload did not return success=true and a URL.'
    }
    $uploadedUri = [Uri]$Result.json.url
    if ($uploadedUri.GetLeftPart([UriPartial]::Authority) -ne $BaseUrl -or
        -not $uploadedUri.AbsolutePath.StartsWith('/uploads/', [StringComparison]::Ordinal) -or
        -not $uploadedUri.AbsolutePath.EndsWith('.png', [StringComparison]::Ordinal)) {
        throw 'Upload URL does not point to a normalized PNG on this isolated API.'
    }
    $served = $http.GetByteArrayAsync($uploadedUri).GetAwaiter().GetResult()
    $servedHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($served))
    if ($servedHash -ne $pngHash) { throw 'Downloaded upload bytes differ from the original PNG.' }
    return @{ status = $Result.status; servedBytes = $served.Length; sha256 = $servedHash }
}

function Assert-RejectedUpload {
    param([hashtable]$RequestParameters, [int]$ExpectedStatus, [string]$ExpectedCode, [switch]$AllowEmptyBody)
    $before = Get-UploadSnapshot
    $result = Invoke-UploadRequest @RequestParameters
    if ($result.status -ne $ExpectedStatus) { throw "Expected HTTP $ExpectedStatus; received HTTP $($result.status)." }
    if (($null -eq $result.json -and -not $AllowEmptyBody) -or
        ($null -ne $result.json -and $result.json.success -ne $false)) { throw 'Rejected upload did not return success=false.' }
    if ($ExpectedCode -and $result.json.code -ne $ExpectedCode) { throw "Rejected upload did not return code=$ExpectedCode." }
    if ($UploadDirectory -and (Get-UploadSnapshot) -ne $before) { throw 'Rejected request changed the isolated uploads directory.' }
    return @{ status = $result.status; filesUnchangedChecked = [bool]$UploadDirectory }
}

$fatalError = $null
$databaseConfigured = $null
try {
    $setup = $http.GetStringAsync("$BaseUrl/api/setup/database").GetAwaiter().GetResult() | ConvertFrom-Json -AsHashtable
    $databaseConfigured = [bool]$setup.configured
    if ($setup.configured -or $setup.server -or $setup.database -or $setup.username) {
        throw 'Refusing upload smoke tests: the API has database configuration; use an isolated package with Database = null.'
    }
    $checks.Add([ordered]@{ name = 'isolated-api-database-unconfigured'; passed = $true })

    Test-UploadCase 'anonymous-png-without-antiforgery-round-trips' {
        Assert-UploadedImage (Invoke-UploadRequest)
    }
    Test-UploadCase 'uppercase-png-extension-normalized-and-round-trips' {
        Assert-UploadedImage (Invoke-UploadRequest -FileName 'test.PNG')
    }
    Test-UploadCase 'empty-image-rejected' {
        Assert-RejectedUpload @{ Bytes = [byte[]]::new(0) } 400
    }
    Test-UploadCase 'unsupported-extension-rejected' {
        Assert-RejectedUpload @{ FileName = 'test.txt' } 400
    }
    Test-UploadCase 'missing-file-rejected' {
        Assert-RejectedUpload @{ MissingFile = $true } 400
    }
    Test-UploadCase 'wrong-form-field-rejected' {
        Assert-RejectedUpload @{ FieldName = 'photo' } 400
    }
    Test-UploadCase 'wrong-content-type-rejected' {
        # Framework binding rejects non-multipart content before the handler,
        # returning HTTP 415 without requiring the application's JSON envelope.
        Assert-RejectedUpload @{ WrongContentType = $true } 415 -AllowEmptyBody
    }
    Test-UploadCase 'invalid-bearer-rejected-before-upload' {
        Assert-RejectedUpload @{ InvalidBearer = $true } 401 'session_expired'
    }
} catch {
    $fatalError = $_.Exception.Message
    $checks.Add([ordered]@{ name = 'preflight-or-run-failure'; passed = $false; error = $fatalError })
} finally {
    $http.Dispose()
}

$failed = @($checks | Where-Object { -not $_.passed })
$result = [ordered]@{
    passed = $failed.Count -eq 0
    completedAtUtc = [DateTime]::UtcNow.ToString('O')
    baseUrl = $BaseUrl
    databaseConfigured = $databaseConfigured
    cookieAuthenticationUsed = $false
    antiforgeryTokenUsed = $false
    rejectedUploadDirectoryChecked = [bool]$UploadDirectory
    checks = @($checks.ToArray())
    limits = @('Real HTTP against an isolated, unconfigured API; valid database-backed sessions and external hosting are not covered.')
}
$json = ConvertTo-Json -InputObject $result -Depth 7
if ($Evidence) { Set-Content -LiteralPath $Evidence -Value $json -Encoding utf8 }
$json
if ($failed.Count -gt 0) {
    throw "Media upload smoke failed: $($failed.Count) of $($checks.Count) checks failed. $($failed[0].error)"
}
