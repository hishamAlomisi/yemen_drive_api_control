param(
    [Parameter(Mandatory = $true)][string]$WwwRoot,
    [string[]]$EntryPages = @('admin.html'),
    [string]$BaseUrl,
    [string]$Evidence,
    [int]$TimeoutSeconds = 5
)
$ErrorActionPreference = 'Stop'
$WwwRoot = [IO.Path]::GetFullPath($WwwRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $WwwRoot -PathType Container)) { throw "Static root does not exist: $WwwRoot" }
$rootPrefix = $WwwRoot + [IO.Path]::DirectorySeparatorChar
$documentOrigin = [uri]'http://static-assets.invalid/'
$assets = [ordered]@{}
$queue = [Collections.Generic.Queue[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$attributePattern = @'
(?<name>[\w:-]+)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<uq>[^\s"'=<>`]+))
'@
$htmlPattern = @'
(?is)<!--.*?-->|<script\b(?<script>(?:"[^"]*"|'[^']*'|[^'">])*)>(?:.*?)</script\s*>|<style\b[^>]*>(?<style>.*?)</style\s*>|<(?<tag>[a-z][\w:-]*)\b(?<attrs>(?:"[^"]*"|'[^']*'|[^'">])*)>
'@
$cssUrlPattern = @'
(?is)url\(\s*(?:"(?<dq>(?:\\.|[^"])*)"|'(?<sq>(?:\\.|[^'])*)'|(?<uq>[^)]*?))\s*\)
'@
$cssImportPattern = @'
(?is)@import\s+(?:"(?<dq>(?:\\.|[^"])*)"|'(?<sq>(?:\\.|[^'])*)')
'@

function Get-QuotedValue($Match) {
    foreach ($name in @('dq', 'sq', 'uq')) {
        if ($Match.Groups[$name].Success) { return $Match.Groups[$name].Value }
    }
}

function Add-Asset([string]$Reference, [uri]$Source, [string]$Kind = '') {
    $reference = [Net.WebUtility]::HtmlDecode($Reference).Trim()
    if (-not $reference -or $reference.StartsWith('#') -or $reference.StartsWith('//') -or $reference -match '^[a-z][a-z0-9+.-]*:') { return }
    try { $uri = [uri]::new($Source, $reference) } catch { $errors.Add("Invalid asset URL '$reference' in $($Source.PathAndQuery)"); return }
    $url = $uri.PathAndQuery
    if ($assets.Contains($url)) { return }
    $relative = [uri]::UnescapeDataString($uri.AbsolutePath).TrimStart('/').Replace('/', [IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $WwwRoot $relative))
    if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { $errors.Add("Asset escapes static root: $url"); return }
    $asset = [ordered]@{ url = $url; relativePath = $relative.Replace('\', '/'); kind = $Kind;
        referencedBy = $Source.PathAndQuery; exists = (Test-Path -LiteralPath $path -PathType Leaf); passed = $false }
    $assets[$url] = $asset
    if (-not $asset.exists) { $errors.Add("Missing static asset $url (referenced by $($asset.referencedBy))"); return }
    $asset.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset.passed = $true
    $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
    if ($extension -in @('.html', '.htm', '.css')) { $queue.Enqueue(@{ uri = $uri; path = $path; extension = $extension }) }
}

function Add-CssAssets([string]$Text, [uri]$Source) {
    $css = [regex]::Replace($Text, '(?s)/\*.*?\*/', '')
    foreach ($pattern in @($cssUrlPattern, $cssImportPattern)) {
        foreach ($match in [regex]::Matches($css, $pattern)) {
            $reference = Get-QuotedValue $match
            $reference = [regex]::Replace($reference, '\\([0-9a-fA-F]{1,6})\s?', { param($m) [char][Convert]::ToInt32($m.Groups[1].Value, 16) })
            $reference = [regex]::Replace($reference, '\\([^\r\n])', '$1')
            Add-Asset $reference $Source 'css-resource'
        }
    }
}

function Add-HtmlAssets([string]$Text, [uri]$Source) {
    foreach ($match in [regex]::Matches($Text, $htmlPattern)) {
        if ($match.Value.StartsWith('<!--')) { continue }
        if ($match.Groups['style'].Success) { Add-CssAssets $match.Groups['style'].Value $Source; continue }
        $tag = $match.Groups['tag'].Value.ToLowerInvariant()
        $attributeText = $match.Groups['attrs'].Value
        if ($match.Groups['script'].Success) { $tag = 'script'; $attributeText = $match.Groups['script'].Value }
        $attributes = @{}
        foreach ($attribute in [regex]::Matches($attributeText, $attributePattern)) {
            $attributes[$attribute.Groups['name'].Value] = Get-QuotedValue $attribute
        }
        if ($tag -eq 'script' -and $attributes.src) { Add-Asset $attributes.src $Source 'script' }
        if ($tag -eq 'link' -and $attributes.href -and $attributes.rel -match '(?i)(^|\s)(stylesheet|icon|apple-touch-icon|mask-icon|preload|modulepreload)(\s|$)') { Add-Asset $attributes.href $Source 'link' }
        if ($tag -in @('img', 'source', 'input') -and $attributes.src) { Add-Asset $attributes.src $Source 'image' }
        if ($tag -eq 'video' -and $attributes.poster) { Add-Asset $attributes.poster $Source 'image' }
        if ($tag -eq 'image') {
            if ($attributes.href) { Add-Asset $attributes.href $Source 'image' }
            if ($attributes['xlink:href']) { Add-Asset $attributes['xlink:href'] $Source 'image' }
        }
        if ($attributes.style) { Add-CssAssets $attributes.style $Source }
        if ($tag -in @('img', 'source') -and $attributes.srcset) {
            # Follow HTML candidate boundaries; a data URL's comma is part of its URL.
            $srcset = $attributes.srcset
            $position = 0
            while ($position -lt $srcset.Length) {
                while ($position -lt $srcset.Length -and ([char]::IsWhiteSpace($srcset[$position]) -or $srcset[$position] -eq ',')) { $position++ }
                $start = $position
                while ($position -lt $srcset.Length -and -not [char]::IsWhiteSpace($srcset[$position])) { $position++ }
                $url = $srcset.Substring($start, $position - $start)
                Add-Asset $url.TrimEnd(',') $Source 'image'
                if ($url.EndsWith(',')) { continue }
                $parentheses = 0
                while ($position -lt $srcset.Length) {
                    $character = $srcset[$position++]
                    if ($character -eq '(') { $parentheses++ }
                    if ($character -eq ')') { $parentheses-- }
                    if ($character -eq ',' -and $parentheses -le 0) { break }
                }
            }
        }
    }
}

foreach ($entry in $EntryPages) { Add-Asset $entry $documentOrigin 'document' }
while ($queue.Count) {
    $document = $queue.Dequeue()
    $text = [IO.File]::ReadAllText($document.path)
    if ($document.extension -eq '.css') { Add-CssAssets $text $document.uri }
    else { Add-HtmlAssets $text $document.uri }
}

$mimeTypes = @{
    '.html' = @('text/html'); '.htm' = @('text/html'); '.css' = @('text/css')
    '.js' = @('text/javascript', 'application/javascript'); '.mjs' = @('text/javascript', 'application/javascript')
    '.png' = @('image/png'); '.jpg' = @('image/jpeg'); '.jpeg' = @('image/jpeg'); '.gif' = @('image/gif')
    '.svg' = @('image/svg+xml'); '.webp' = @('image/webp'); '.avif' = @('image/avif')
    '.ico' = @('image/x-icon', 'image/vnd.microsoft.icon'); '.woff' = @('font/woff', 'application/font-woff')
    '.woff2' = @('font/woff2'); '.ttf' = @('font/ttf', 'application/x-font-ttf'); '.json' = @('application/json')
    '.eot' = @('application/vnd.ms-fontobject'); '.cur' = @('image/x-icon', 'image/vnd.microsoft.icon')
    '.webmanifest' = @('application/manifest+json', 'application/json')
}
if ($BaseUrl) {
    $origin = [uri]$BaseUrl
    if ($origin.Scheme -notin @('http', 'https')) { throw 'BaseUrl must use HTTP or HTTPS.' }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        foreach ($asset in $assets.Values) {
            if (-not $asset.exists) { continue }
            $response = $null
            try {
                $response = $http.GetAsync([uri]::new($origin, $asset.url)).GetAwaiter().GetResult()
                $asset.status = [int]$response.StatusCode
                $asset.contentType = $response.Content.Headers.ContentType.MediaType
                if ($asset.status -ne 200) { throw "HTTP $($asset.status)" }
                $extension = [IO.Path]::GetExtension($asset.relativePath).ToLowerInvariant()
                if ($mimeTypes.ContainsKey($extension) -and $asset.contentType -notin $mimeTypes[$extension]) { throw "Unexpected content type '$($asset.contentType)' for $extension" }
                if (-not $asset.contentType -or ($extension -notin @('.html', '.htm') -and $asset.contentType -eq 'text/html')) { throw 'Asset response has no MIME type or unexpectedly contains HTML' }
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                $asset.servedSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                if ($asset.servedSha256 -ne $asset.sha256) { throw 'Served content differs from packaged file' }
            } catch {
                $asset.passed = $false
                $asset.error = $_.Exception.Message
                $errors.Add("Static HTTP check failed for $($asset.url): $($asset.error)")
            } finally { if ($response) { $response.Dispose() } }
        }
    } finally { $http.Dispose() }
}
$result = [ordered]@{ passed = $errors.Count -eq 0; checkedAtUtc = [DateTime]::UtcNow.ToString('O');
    wwwroot = $WwwRoot; baseUrl = $BaseUrl; entryPages = $EntryPages; assetCount = $assets.Count;
    assets = @($assets.Values); errors = $errors.ToArray() }
$json = $result | ConvertTo-Json -Depth 8
if ($Evidence) { $json | Set-Content -LiteralPath $Evidence -Encoding utf8 }
$json
if (-not $result.passed) { throw ($errors -join [Environment]::NewLine) }
