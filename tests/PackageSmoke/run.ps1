param(
    [string]$ExistingFeed = ''
)

# Pack-and-consume smoke: builds and runs the standalone Smoke consumer from packages only. By
# default it packs each provider chain to a local feed; CI can pass -ExistingFeed to consume the
# packages already produced by the preceding pack step. Proves a single provider PackageReference
# delivers the runtime, the [Job] source generator, and the ACTA analyzers - no project references.
# Repeats the consume for every provider so each shippable provider package is proven self-contained.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$feed = Join-Path $PSScriptRoot 'feed'

# Sqlite is the easiest to consume (no server), so it runs first; the providers differ only in their
# native data-access dependency, and the consumer never opens a connection.
$providers = @('Acta.Postgres', 'Acta.Sqlite', 'Acta.SqlServer')

foreach ($stale in @($feed, "$PSScriptRoot/.packages", "$PSScriptRoot/Smoke/bin", "$PSScriptRoot/Smoke/obj")) {
    if (Test-Path $stale) { Remove-Item -Recurse -Force $stale }
}

if ($ExistingFeed) {
    $existingFeedPath = Resolve-Path $ExistingFeed
    New-Item -ItemType Directory -Force $feed | Out-Null
    Copy-Item (Join-Path $existingFeedPath '*.nupkg') -Destination $feed
}
else {
    # Acta packs the generator DLL from src/Acta.Generators/bin/$Configuration; build it
    # first so the pack never picks up a stale or missing assembly.
    dotnet build "$root/src/Acta.Generators/Acta.Generators.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'build failed: Acta.Generators' }

    # Redis and Testing are packed here and consumed below; Acta.AspNetCore needs Node-built dashboard
    # assets so it is only consumed when an existing feed (CI) already contains it.
    foreach ($project in @('Acta', 'Acta.Runtime', 'Acta.Relational', 'Acta.Redis', 'Acta.Testing') + $providers) {
        dotnet pack "$root/src/$project/$project.csproj" -c Release -o $feed --nologo
        if ($LASTEXITCODE -ne 0) { throw "pack failed: $project" }
    }
}

# Metadata gate: every packed nupkg must carry the shared NuGet metadata and a MinVer tag-derived
# version. This is the durable form of the releasing.md "NuGet metadata checked" line.
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in Get-ChildItem $feed -Filter '*.nupkg') {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = $zip.Entries | ForEach-Object { $_.FullName }
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' }
        $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $meta = $nuspec.package.metadata

        $failures = @()
        if (-not $meta.description) { $failures += 'missing description' }
        if ($meta.license.'#text' -ne 'Apache-2.0') { $failures += "license '$($meta.license.'#text')' is not Apache-2.0" }
        if ($meta.repository.url -ne 'https://github.com/acta-dotnet/acta') { $failures += "repository url '$($meta.repository.url)'" }
        if (-not $meta.readme -or $entries -notcontains $meta.readme) { $failures += 'readme not declared or not packed' }
        if ($meta.version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { $failures += "version '$($meta.version)' is not a MinVer semver" }
        if ($failures) { throw "package metadata failed for $($package.Name): $($failures -join '; ')" }
    }
    finally { $zip.Dispose() }
}
Write-Host "PackageSmoke: metadata verified for $((Get-ChildItem $feed -Filter '*.nupkg').Count) packages"

# A private packages folder for the consumer only: the freshly packed version must never be shadowed
# by an immutable copy in the user/machine global cache (NUGET_PACKAGES outranks nuget.config).
$savedPackagesFolder = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
try {
    foreach ($provider in $providers) {
        # MinVer makes the packed version dynamic, so the consumer pins exactly what was just produced.
        # The $feed wipe above guarantees a single package per provider.
        $packages = @(Get-ChildItem $feed -Filter "$provider.*.nupkg")
        if ($packages.Count -ne 1) { throw "expected exactly one $provider package, found $($packages.Count)" }
        $version = $packages[0].BaseName -replace ('^' + [regex]::Escape($provider) + '\.'), ''

        # A fresh obj/bin per provider so the previous provider's restore graph never leaks in.
        foreach ($stale in @("$PSScriptRoot/Smoke/bin", "$PSScriptRoot/Smoke/obj")) {
            if (Test-Path $stale) { Remove-Item -Recurse -Force $stale }
        }

        Write-Host "PackageSmoke: consuming $provider $version"
        dotnet run --project "$PSScriptRoot/Smoke/Smoke.csproj" -c Release --nologo `
            -p:ActaPackageVersion=$version -p:ActaProvider=$provider
        if ($LASTEXITCODE -ne 0) { throw "package smoke failed: $provider" }
    }

    # Non-provider packages, each layered once on the cheapest provider (Sqlite) so their dependency
    # graphs are consume-proven from the feed too. AspNetCore only exists in a CI-produced feed.
    $extras = @('Acta.Redis', 'Acta.Testing')
    if (Get-ChildItem $feed -Filter 'Acta.AspNetCore.*.nupkg' -ErrorAction SilentlyContinue) { $extras += 'Acta.AspNetCore' }
    foreach ($extra in $extras) {
        $packages = @(Get-ChildItem $feed -Filter "$extra.*.nupkg")
        if ($packages.Count -ne 1) { throw "expected exactly one $extra package, found $($packages.Count)" }
        $version = $packages[0].BaseName -replace ('^' + [regex]::Escape($extra) + '\.'), ''

        foreach ($stale in @("$PSScriptRoot/Smoke/bin", "$PSScriptRoot/Smoke/obj")) {
            if (Test-Path $stale) { Remove-Item -Recurse -Force $stale }
        }

        Write-Host "PackageSmoke: consuming $extra $version (on Acta.Sqlite)"
        dotnet run --project "$PSScriptRoot/Smoke/Smoke.csproj" -c Release --nologo `
            -p:ActaPackageVersion=$version -p:ActaProvider=Acta.Sqlite -p:ActaExtra=$extra
        if ($LASTEXITCODE -ne 0) { throw "package smoke failed: $extra" }
    }
}
finally {
    $env:NUGET_PACKAGES = $savedPackagesFolder
}
Write-Host 'PackageSmoke: all green'
