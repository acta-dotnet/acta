[CmdletBinding()]
param(
    [string]$PreviousRef = 'v1.0.0-rc.3',
    [ValidateSet('pg', 'mssql')]
    [string[]]$Providers = @('pg', 'mssql')
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$artifactRoot = Join-Path $taskRoot "artifacts/rolling-upgrade/$runId"
$previousRoot = Join-Path $artifactRoot 'previous'
$currentBuildCommit = git -C $taskRoot rev-parse HEAD
$currentBuildDirty = [bool](git -C $taskRoot status --porcelain)
New-Item -ItemType Directory -Path $previousRoot -Force | Out-Null
$archive = Join-Path $artifactRoot 'previous.zip'
git -C $taskRoot archive --format=zip "--output=$archive" $PreviousRef
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the previous release.' }
Expand-Archive -LiteralPath $archive -DestinationPath $previousRoot

# Separate consumer directories and output folders prevent incremental builds from substituting the
# current assemblies for the previous ones. No runtime source in either tree is edited.
foreach ($generation in @('previous', 'current')) {
    $consumer = Join-Path $artifactRoot "tests/$generation-consumer"
    New-Item -ItemType Directory -Path $consumer -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Smoke.csproj'), (Join-Path $PSScriptRoot 'Program.cs') -Destination $consumer
    $sourceRoot = if ($generation -eq 'previous') { $previousRoot } else { $taskRoot }
    $buildLog = Join-Path $artifactRoot "$generation-build.log"
    dotnet build (Join-Path $consumer 'Smoke.csproj') "-p:ActaSourceRoot=$sourceRoot" -v minimal *> $buildLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog -Tail 30; throw "$generation consumer build failed." }
}

$previousBinary = Join-Path $artifactRoot 'tests/previous-consumer/bin/Debug/net10.0/Smoke.dll'
$currentBinary = Join-Path $artifactRoot 'tests/current-consumer/bin/Debug/net10.0/Smoke.dll'
$results = @()
foreach ($provider in $Providers) {
    $envName = if ($provider -eq 'pg') { 'ACTA_TEST_PG' } else { 'ACTA_TEST_MSSQL' }
    if (-not [Environment]::GetEnvironmentVariable($envName)) { throw "$envName is required." }
    $schema = "acta_upgrade_$runId"
    # Begin with the previous SQL, upgrade with the current full package, then keep migrations off.
    $phases = @(
        @{ Generation = 'previous'; Binary = $previousBinary; Mode = 'provision'; Profile = 'Direct' },
        @{ Generation = 'current'; Binary = $currentBinary; Mode = 'old-sql-rejected'; Profile = 'Direct'; ExpectedFailure = $true },
        @{ Generation = 'current'; Binary = $currentBinary; Mode = 'provision'; Profile = 'Direct' }
    )
    foreach ($profile in @('Buffered', 'Direct', 'Bulk')) {
        $phases += @{ Generation = 'previous'; Binary = $previousBinary; Mode = 'verify'; Profile = $profile }
        $phases += @{ Generation = 'current'; Binary = $currentBinary; Mode = 'verify'; Profile = $profile }
    }
    foreach ($phase in $phases) {
        $label = "$provider-$($phase.Generation)-$($phase.Mode)-$($phase.Profile)"
        $log = Join-Path $artifactRoot "$label.log"
        dotnet $phase.Binary $provider $schema $phase.Mode $phase.Profile *> $log
        if ($phase.ExpectedFailure) {
            if ($LASTEXITCODE -eq 0 -or -not (Select-String -LiteralPath $log -SimpleMatch 'records no Acta object package')) {
                Get-Content -LiteralPath $log -Tail 30
                throw "$label did not fail at the expected read-only SQL compatibility preflight."
            }
            Write-Output "PASS $label`: incompatible old SQL rejected before worker startup."
        } else {
            if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 30; throw "$label failed." }
            Get-Content -LiteralPath $log
        }
        $results += @{ Provider = $provider; Generation = $phase.Generation; Mode = $phase.Mode; Profile = $phase.Profile; Passed = $true }
    }
}
$evidence = @{
    PreviousRef = $PreviousRef
    PreviousCommit = (git -C $taskRoot rev-parse "$PreviousRef^{commit}")
    CurrentBuildCommit = $currentBuildCommit
    CurrentBuildWorktreeDirty = $currentBuildDirty
    CurrentEndCommit = (git -C $taskRoot rev-parse HEAD)
    PreviousRuntimeHash = (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'tests/previous-consumer/bin/Debug/net10.0/Acta.Runtime.dll')).Hash
    CurrentRuntimeHash = (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'tests/current-consumer/bin/Debug/net10.0/Acta.Runtime.dll')).Hash
    Results = $results
}
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactRoot 'evidence.json') -Encoding utf8
Write-Output "Rolling-upgrade smoke passed. Evidence: $artifactRoot"
