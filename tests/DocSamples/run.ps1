param(
    [string]$ExistingFeed = '',
    [switch]$AllowSkips
)

# Doc-sample compile gate: extracts every code sample a newcomer meets (llms.txt, README.md,
# docs/quickstart.md, site/start.html) and compiles it as a complete program against the candidate
# packages - the bytes a newcomer restores, never a project reference. By default it packs to a
# local feed; CI passes -ExistingFeed to consume the packages the preceding pack step produced.
# A sample that only looks right (an undefined service, a manifest name that needs the right
# RootNamespace, a missing package line) fails here instead of in a newcomer's first five minutes.
# A run that could not compile every sample is not a pass: it ends nonzero naming what it skipped,
# unless -AllowSkips says an incomplete run was the point. CI never passes that flag.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$feed = Join-Path $PSScriptRoot 'feed'

# One project per sample group, each referencing exactly the packages its own document tells the
# reader to install. Extractor writes the sample sources into <Name>/Generated.
$samples = @(
    @{ Name = 'FirstRun'; Requires = @('Acta.Sqlite', 'Acta.AspNetCore') },
    @{ Name = 'Webhook'; Requires = @('Acta') },
    @{ Name = 'Users'; Requires = @('Acta.Sqlite') }
)

# Package ids share the Acta. prefix (Acta, Acta.Sqlite), so a *.nupkg wildcard cannot tell them
# apart: match the id followed by a version that starts with a digit.
function Get-FeedPackage([string]$id) {
    $pattern = '^' + [regex]::Escape($id) + '\.(\d[0-9A-Za-z.\-+]*)$'
    return @(Get-ChildItem $feed -Filter '*.nupkg' | Where-Object { $_.BaseName -match $pattern })
}

function Get-FeedVersion([string]$id) {
    # MinVer makes the packed version dynamic, so each sample pins exactly what is in the feed.
    $found = Get-FeedPackage $id
    if ($found.Count -ne 1) { throw "expected exactly one $id package in the feed, found $($found.Count)" }
    return $found[0].BaseName.Substring($id.Length + 1)
}

foreach ($stale in @($feed, "$PSScriptRoot/.packages")) {
    if (Test-Path $stale) { Remove-Item -Recurse -Force $stale }
}
foreach ($sample in $samples) {
    foreach ($stale in @("$PSScriptRoot/$($sample.Name)/bin", "$PSScriptRoot/$($sample.Name)/obj", "$PSScriptRoot/$($sample.Name)/Generated")) {
        if (Test-Path $stale) { Remove-Item -Recurse -Force $stale }
    }
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

    # Acta.Sqlite's own dependency chain has to resolve from the local feed too: the source mapping
    # in nuget.config pins every Acta* id here, so an unpacked link would fail the restore.
    foreach ($project in @('Acta', 'Acta.Runtime', 'Acta.Relational', 'Acta.Sqlite')) {
        dotnet pack "$root/src/$project/$project.csproj" -c Release -o $feed --nologo
        if ($LASTEXITCODE -ne 0) { throw "pack failed: $project" }
    }

    # Acta.AspNetCore embeds Node-built dashboard assets. Prebuilt assets (the CI artifact, or a
    # previous local build) pack without npm; otherwise Node builds them; without either, the one
    # sample that maps the dashboard is skipped rather than compiled against a substitute.
    $dashboardIndex = Join-Path $root 'src/Acta.AspNetCore/DashboardApp/dist/index.html'
    $nodePresent = $null -ne (Get-Command node -ErrorAction SilentlyContinue)
    if ((Test-Path $dashboardIndex) -or $nodePresent) {
        $skipNpm = if (Test-Path $dashboardIndex) { 'true' } else { 'false' }
        dotnet pack "$root/src/Acta.AspNetCore/Acta.AspNetCore.csproj" -c Release -o $feed --nologo -p:ActaDashboardSkipNpm=$skipNpm
        if ($LASTEXITCODE -ne 0) { throw 'pack failed: Acta.AspNetCore' }
    }
    else {
        Write-Warning 'DocSamples: no dashboard assets and no Node on PATH, so Acta.AspNetCore is not packed and the first-run sample cannot be compiled. Run npm ci and npm run build in src/Acta.AspNetCore/DashboardApp, or pass -AllowSkips to accept a partial run.'
    }
}

# Extraction is the drift gates' own utility (tests/Acta.Tests/Docs/DocSampleExtraction.cs), linked
# into this tool: one parser, so a sample cannot compile here under rules the unit gates never applied.
dotnet run --project "$PSScriptRoot/Extractor/Extractor.csproj" -c Release -- "$PSScriptRoot"
if ($LASTEXITCODE -ne 0) { throw 'doc sample extraction failed' }

# A private packages folder for the samples only: the freshly packed version must never be shadowed
# by an immutable copy in the user/machine global cache (NUGET_PACKAGES outranks nuget.config).
$savedPackagesFolder = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
$compiled = @()
$skipped = @()
$failed = @()
try {
    foreach ($sample in $samples) {
        $missing = @($sample.Requires | Where-Object { (Get-FeedPackage $_).Count -eq 0 })
        if ($missing.Count -gt 0) {
            # A CI feed carries every shippable package, so a gap there is a broken gate, not a local limitation.
            if ($ExistingFeed) { throw "$($sample.Name) requires $($missing -join ', '), absent from $ExistingFeed" }
            Write-Warning "DocSamples: skipping $($sample.Name); its feed is missing $($missing -join ', ')"
            $skipped += $sample.Name
            continue
        }

        $version = Get-FeedVersion $sample.Requires[0]
        Write-Host "DocSamples: compiling $($sample.Name) against $($sample.Requires -join ' + ') $version"
        # Compile only: the first-run program blocks on WaitForShutdown, and what a sample must prove
        # is that its published text is a complete program. Warnings fail the build: a newcomer's own
        # project would not stop on one, but a sample that compiles with a warning is a sample that
        # teaches it, and the ACTA analyzers ship in these packages to be listened to.
        dotnet build "$PSScriptRoot/$($sample.Name)/$($sample.Name).csproj" -c Release --nologo -warnaserror -p:ActaPackageVersion=$version
        # Every sample is built before the run fails, so one broken document does not hide the rest.
        if ($LASTEXITCODE -ne 0) { $failed += $sample.Name } else { $compiled += $sample.Name }
    }
}
finally {
    $env:NUGET_PACKAGES = $savedPackagesFolder
}

Write-Host "DocSamples: compiled $($compiled -join ', ')"
if ($failed.Count -gt 0) {
    throw "doc samples failed to compile: $($failed -join ', '). The published sample is the contract: fix the document, not the harness."
}
if ($skipped.Count -gt 0) {
    # Silence here is how an unproven sample ships: the gate would have printed success while the
    # document it covers was never compiled by anything.
    $unproven = "DocSamples: NOT COMPILED in this run: $($skipped -join ', '). The samples those projects cover are unproven."
    if (-not $AllowSkips) {
        throw "$unproven Install what the warnings above name (Node 20.19+ or 22.12+ on PATH, or prebuilt dashboard assets), or pass -AllowSkips to accept a partial run."
    }
    Write-Warning "$unproven Accepted because -AllowSkips was passed."
    Write-Host "DocSamples: green except $($skipped -join ', ')"
    exit 0
}
Write-Host 'DocSamples: all green'
