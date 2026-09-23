[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageVersion, [string]$ProxyImage = 'nginx:alpine')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$output = Join-Path $taskRoot "artifacts/deployment-smoke/$runId"
New-Item -ItemType Directory -Path $output -Force | Out-Null
dotnet build (Join-Path $PSScriptRoot 'DeploymentSmoke.csproj') "-p:ActaPackageVersion=$PackageVersion" "-p:RestorePackagesPath=$output/packages" -v minimal *> (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'build.log') -Tail 30; throw 'Packaged consumer build failed.' }
$env:ACTA_SMOKE_OUTPUT = $output
$env:ACTA_SMOKE_KEY = [Guid]::NewGuid().ToString('N')
$binary = Join-Path $PSScriptRoot 'bin/Debug/net10.0/DeploymentSmoke.dll'
$appProcess = $null
$proxyStarted = $false
$container = "acta-deployment-smoke-$runId"
$checks = @()
try {
    $appProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $binary + '"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'app.log') -RedirectStandardError (Join-Path $output 'app-errors.log')
    $readyPath = Join-Path $output 'ready.json'
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $readyPath)) {
        if ($appProcess.HasExited -or [DateTime]::UtcNow -gt $readyDeadline) {
            Get-Content (Join-Path $output 'app-errors.log') -Tail 30
            throw 'Packaged app failed to start and complete its binary job.'
        }
        Start-Sleep -Milliseconds 100
    }
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    $proxyConfig = (Get-Content (Join-Path $PSScriptRoot 'nginx.conf') -Raw).Replace('__PORT__', [string]$ready.Port)
    $configPath = Join-Path $output 'nginx.conf'
    [IO.File]::WriteAllText($configPath, $proxyConfig)
    docker run -d --name $container -p '127.0.0.1::80' --mount "type=bind,source=$configPath,target=/etc/nginx/nginx.conf,readonly" $ProxyImage | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Proxy container failed to start.' }
    $proxyStarted = $true
    $binding = docker port $container '80/tcp'
    $base = "http://$binding/operations/acta"
    $auth = @{ 'X-Smoke-Key' = $env:ACTA_SMOKE_KEY }
    $spoof = @{ 'X-Forwarded-User' = 'operator'; 'X-Forwarded-Proto' = 'https' }
    $request = { param($url, $headers, $method = 'GET') Invoke-WebRequest -Uri $url -Headers $headers -Method $method -SkipHttpErrorCheck -NoProxy -TimeoutSec 10 }
    $proxyReady = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = & $request $base $spoof
            if ([int]$response.StatusCode -eq 401) { $proxyReady = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    if (-not $proxyReady) { throw 'The proxy did not become ready with an authorization challenge.' }
    foreach ($path in @('', '/assets/anything.js', '/api/v1/jobs', "/api/v1/jobs/$($ready.JobRef)/input", "/api/v1/jobs/$($ready.JobRef)/detail")) {
        $response = & $request "$base$path" $spoof
        if ([int]$response.StatusCode -ne 401) { throw "Anonymous request $path returned $($response.StatusCode), expected 401." }
        $checks += @{ Path = $path; Authenticated = $false; Status = 401 }
    }
    $html = & $request $base $auth
    if ([int]$html.StatusCode -ne 200) { throw 'Authorized dashboard HTML failed.' }
    $checks += @{ Path = ''; Authenticated = $true; Status = 200 }
    $asset = [regex]::Match($html.Content, 'src="([^"]+\.js)"').Groups[1].Value
    if (-not $asset) { throw 'Packaged dashboard HTML had no JavaScript asset.' }
    $assetUrl = if ($asset.StartsWith('/')) { "http://$binding$asset" } else { "$base/$asset" }
    $assetResponse = & $request $assetUrl $auth
    if ([int]$assetResponse.StatusCode -ne 200 -or $assetResponse.RawContentLength -lt 1000) { throw 'Authorized packaged JavaScript asset failed.' }
    $checks += @{ Path = $asset; Authenticated = $true; Status = 200; Bytes = $assetResponse.RawContentLength }
    $anonymousAsset = & $request $assetUrl $spoof
    if ([int]$anonymousAsset.StatusCode -ne 401) { throw 'The real packaged JavaScript asset bypassed authorization.' }
    $checks += @{ Path = $asset; Authenticated = $false; Status = 401 }
    $inputResponse = & $request "$base/api/v1/jobs/$($ready.JobRef)/input" $auth
    $detailResponse = & $request "$base/api/v1/jobs/$($ready.JobRef)/detail" $auth
    if ([int]$inputResponse.StatusCode -ne 200 -or [int]$detailResponse.StatusCode -ne 200) { throw 'Authorized payload reads failed.' }
    if ($detailResponse.Headers['Cache-Control'] -notcontains 'no-store') { throw 'Payload detail response was cacheable.' }
    $input = $inputResponse.Content | ConvertFrom-Json
    $detail = $detailResponse.Content | ConvertFrom-Json
    if ($input.base64 -ne 'AQIDBA==' -or $detail.input.base64 -ne 'AQIDBA==' -or $detail.result.base64 -ne 'BQYHCA==') { throw 'Binary input/result download-source bytes differ.' }
    $checkpoint = $detail.checkpoints | Where-Object name -eq 'binary-checkpoint'
    if ($checkpoint.value.base64 -ne 'CQoLDA==') { throw 'Binary checkpoint download-source bytes differ.' }
    $checks += @{ Path = 'input/detail'; Authenticated = $true; Status = 200; InputResultCheckpointBytesVerified = $true }
    $capabilities = (& $request "$base/api/v1/capabilities" $auth).Content | ConvertFrom-Json
    if ($capabilities.controlsEnabled) { throw 'Read-only deployment unexpectedly enabled controls.' }
    $checks += @{ Path = 'capabilities'; Authenticated = $true; Status = 200; ControlsEnabled = $false }
    $control = & $request "$base/api/v1/jobs/$($ready.JobRef)/cancel" $auth 'POST'
    if ([int]$control.StatusCode -ne 404) { throw 'Disabled control endpoint was mapped.' }
    $checks += @{ Path = 'cancel'; Authenticated = $true; Status = 404 }
    $imageId = docker inspect --format '{{.Image}}' $container
    $packageHashes = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'artifacts/packages') -Filter "*.$PackageVersion.nupkg" | Get-FileHash | Select-Object @{Name='Package';Expression={Split-Path $_.Path -Leaf}},Hash
    @{ PackageVersion = $PackageVersion; PackageHashes = $packageHashes; ProxyImage = $ProxyImage; ProxyImageId = $imageId; BasePath = '/operations/acta'; Checks = $checks; TestAuthentication = 'Ephemeral test header; forwarded user ignored'; Tls = $false } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'evidence.json') -Encoding utf8
    Write-Output "PASS packaged Kestrel + nginx deployment: protected HTML/assets/API, binary input/result/checkpoint reads, and disabled controls. Evidence: $output"
} finally {
    if ($proxyStarted) {
        docker logs $container *> (Join-Path $output 'proxy.log')
        docker rm -f $container 2>$null | Out-Null
    }
    if ($appProcess -and -not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
    Remove-Item Env:ACTA_SMOKE_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:ACTA_SMOKE_OUTPUT -ErrorAction SilentlyContinue
}
