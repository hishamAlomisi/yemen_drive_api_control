param([string]$EvidenceRoot = ('E:\Driver\work\static-asset-regression-' + [Guid]::NewGuid().ToString('N')))
$ErrorActionPreference = 'Stop'
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (-not $EvidenceRoot.StartsWith('E:\Driver\work\static-asset-regression-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an isolated regression directory.' }
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Regression directory already exists.' }
$wwwroot = Join-Path $EvidenceRoot 'wwwroot'
foreach ($relative in @('styles\theme', 'icons', 'fonts', 'vendor')) { New-Item -ItemType Directory -Path (Join-Path $wwwroot $relative) -Force | Out-Null }
$html = @'
<!doctype html><html><head>
<link rel="stylesheet" href="/styles/main.css?v=1">
<link rel="icon" href="/icons/logo.png?v=1">
<link rel="canonical" href="/not-an-asset">
<script src="/vendor/signalr.min.js" defer></script>
<!-- <script src="/comment-only.js"></script> -->
<script>const sample = '<img src="/javascript-string.png">';</script>
<style>.inline { background: url('/icons/logo.png'); }</style>
</head><body><img src="/icons/logo.png" srcset="data:image/png;base64,AA== 1x, /icons/retina.png 2x"></body></html>
'@
[IO.File]::WriteAllText((Join-Path $wwwroot 'admin.html'), $html)
[IO.File]::WriteAllText((Join-Path $wwwroot 'styles\main.css'), '@import "theme/base.css"; @import url("theme/base.css?theme=1"); .a { background: url(../icons/logo.png); } .b { behavior: url(#default#VML); }')
[IO.File]::WriteAllText((Join-Path $wwwroot 'styles\theme\base.css'), '@font-face { src: url(../../fonts/test.woff2); } .a { background: url("../../icons/required.png"); }')
foreach ($relative in @('icons\logo.png', 'icons\retina.png', 'fonts\test.woff2')) { [IO.File]::WriteAllBytes((Join-Path $wwwroot $relative), [byte[]](1, 2, 3, 4)) }
$checker = Join-Path $PSScriptRoot 'static-asset-dependencies.ps1'
$checks = [Collections.Generic.List[string]]::new()
function Assert([bool]$Value, [string]$Description) { if (-not $Value) { throw $Description }; $checks.Add($Description) }
function Inspect([string]$Label, [string]$Url = '') {
    $path = Join-Path $EvidenceRoot ($Label + '.json')
    try { & $checker -WwwRoot $wwwroot -BaseUrl $Url -Evidence $path | Out-Null } catch { }
    if (-not (Test-Path -LiteralPath $path)) { throw "Checker did not write $Label evidence." }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
$missing = Inspect 'missing-assets'
Assert (-not $missing.passed -and ($missing.errors -join ' ') -match '/vendor/signalr.min.js') 'Missing linked vendor script fails file verification'
Assert (($missing.errors -join ' ') -match '/icons/required.png') 'Missing image reached through recursive CSS imports fails verification'
[IO.File]::WriteAllText((Join-Path $wwwroot 'vendor\signalr.min.js'), 'globalThis.signalR = {};')
[IO.File]::WriteAllBytes((Join-Path $wwwroot 'icons\required.png'), [byte[]](4, 3, 2, 1))
$complete = Inspect 'complete-files'
Assert $complete.passed 'Complete local HTML and recursive CSS dependency graph passes'
foreach ($url in @('/styles/main.css?v=1', '/styles/theme/base.css', '/styles/theme/base.css?theme=1', '/icons/retina.png', '/fonts/test.woff2')) {
    Assert ($complete.assets.url -contains $url) "Dependency graph includes $url"
}
Assert (-not ($complete.assets.url -match 'comment-only|javascript-string|not-an-asset|AA==|default')) 'Comments, inline script text, navigation links, data URLs and fragments are not file dependencies'

foreach ($mode in @('good', 'wrong-mime', 'wrong-hash')) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $job = Start-Job -ArgumentList $wwwroot, $port, $mode -ScriptBlock {
        param($root, $port, $mode)
        $ErrorActionPreference = 'Stop'
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        $listener.Start()
        'READY'
        try {
            while ($true) {
                $pending = $listener.AcceptTcpClientAsync()
                while (-not $pending.IsCompleted) { Start-Sleep -Milliseconds 25 }
                $client = $pending.GetAwaiter().GetResult()
                try {
                    $stream = $client.GetStream()
                    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
                    $request = $reader.ReadLine()
                    if (-not $request) { continue }
                    while ($reader.ReadLine()) { }
                    $uri = [uri]::new('http://fixture.invalid' + $request.Split(' ')[1])
                    $relative = [uri]::UnescapeDataString($uri.AbsolutePath).TrimStart('/').Replace('/', '\')
                    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
                    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Unexpected fixture request.' }
                    $bytes = [IO.File]::ReadAllBytes($path)
                    $type = switch ([IO.Path]::GetExtension($path)) { '.html' { 'text/html' }; '.js' { 'text/javascript' }; '.css' { 'text/css' }; '.png' { 'image/png' }; '.woff2' { 'font/woff2' } }
                    if ($relative -eq 'vendor\signalr.min.js' -and $mode -eq 'wrong-mime') { $type = 'text/html' }
                    if ($relative -eq 'vendor\signalr.min.js' -and $mode -eq 'wrong-hash') { $bytes = [Text.Encoding]::UTF8.GetBytes('globalThis.unexpected = true;') }
                    $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: $type`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
                    $stream.Write($header, 0, $header.Length); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
                } finally { $client.Dispose() }
            }
        } finally { $listener.Stop() }
    }
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ((Receive-Job -Job $job -Keep) -notcontains 'READY') {
            if ($job.State -in @('Failed', 'Completed') -or [DateTime]::UtcNow -gt $deadline) { throw 'Local HTTP fixture did not start.' }
            Start-Sleep -Milliseconds 100
        }
        $served = Inspect $mode "http://127.0.0.1:$port"
        if ($mode -eq 'good') { Assert $served.passed 'All dependency HTTP statuses, MIME types and SHA256 hashes pass' }
        if ($mode -eq 'wrong-mime') { Assert (-not $served.passed -and ($served.errors -join ' ') -match 'content type') 'A 200 JavaScript response with HTML MIME type fails' }
        if ($mode -eq 'wrong-hash') { Assert (-not $served.passed -and ($served.errors -join ' ') -match 'differs from packaged file') 'A 200 JavaScript response with changed bytes fails' }
    } finally { Stop-Job $job; Remove-Job $job }
}
$result = @{ passed = $true; checks = $checks.ToArray(); evidenceRoot = $EvidenceRoot }
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding utf8
$result | ConvertTo-Json -Depth 4
