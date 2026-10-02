[CmdletBinding()]
# Until 1.0 ships no earlier release shares this baseline, so the default pairs this tree with itself under
# the overlap phase; from 1.0 the previous release is the pair.
param(
    [string]$PreviousRef = 'HEAD',
    [ValidateSet('pg', 'mssql')]
    [string[]]$Providers = @('pg', 'mssql'),
    [ValidateSet('Sequential', 'Overlap')]
    [string[]]$Phases = @('Overlap')
)

$ErrorActionPreference = 'Stop'
# A release candidate cannot share a database with 1.0: its baseline is refused at startup. The overlap
# phase is for 1.0 and later pairs.
if ($Phases -contains 'Overlap' -and $PreviousRef -match '-rc\.') {
    throw "The overlap phase needs a previous release that can share this database; $PreviousRef is a release candidate."
}
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

$binaries = @{
    previous = Join-Path $artifactRoot 'tests/previous-consumer/bin/Debug/net10.0/Smoke.dll'
    current = Join-Path $artifactRoot 'tests/current-consumer/bin/Debug/net10.0/Smoke.dll'
}
$results = @()

function Add-Result($provider, $phase) {
    $script:results += @{ Provider = $provider; Phase = $phase.Phase; Generation = $phase.Generation; Mode = $phase.Mode; Profile = $phase.Profile; Passed = $true }
}

function Get-PhaseLabel($provider, $phase) { "$provider-$($phase.Phase)-$($phase.Generation)-$($phase.Mode)-$($phase.Profile)" }

# Runs one phase to completion in the foreground.
function Invoke-Phase($provider, $schema, $phase) {
    $label = Get-PhaseLabel $provider $phase
    $log = Join-Path $artifactRoot "$label.log"
    dotnet $binaries[$phase.Generation] $provider $schema $phase.Mode $phase.Profile *> $log
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
    Add-Result $provider $phase
}

# Starts one phase as a background process; Wait-Phase collects it.
function Start-Phase($provider, $schema, $phase) {
    $label = Get-PhaseLabel $provider $phase
    $process = Start-Process -FilePath 'dotnet' -NoNewWindow -PassThru `
        -ArgumentList @($binaries[$phase.Generation], $provider, $schema, $phase.Mode, $phase.Profile) `
        -RedirectStandardOutput (Join-Path $artifactRoot "$label.log") `
        -RedirectStandardError (Join-Path $artifactRoot "$label.err.log")
    # Reading the handle now is what lets ExitCode be read after the process exits.
    $null = $process.Handle
    @{ Process = $process; Phase = $phase; Label = $label }
}

function Wait-Phase($provider, $started, [int]$timeoutSeconds = 180) {
    $log = Join-Path $artifactRoot "$($started.Label).log"
    if (-not $started.Process.WaitForExit($timeoutSeconds * 1000)) {
        $started.Process.Kill()
        throw "$($started.Label) did not finish within $timeoutSeconds seconds."
    }
    if ($started.Process.ExitCode -ne 0) {
        Get-Content -LiteralPath $log, (Join-Path $artifactRoot "$($started.Label).err.log") -Tail 30
        throw "$($started.Label) failed."
    }
    Get-Content -LiteralPath $log
    Add-Result $provider $started.Phase
}

foreach ($provider in $Providers) {
    $envName = if ($provider -eq 'pg') { 'ACTA_TEST_PG' } else { 'ACTA_TEST_MSSQL' }
    if (-not [Environment]::GetEnvironmentVariable($envName)) { throw "$envName is required." }

    if ($Phases -contains 'Sequential') {
        $schema = "acta_upgrade_$runId"
        # Begin with the previous SQL, upgrade with the current full package, then keep migrations off.
        $sequence = @(
            @{ Phase = 'sequential'; Generation = 'previous'; Mode = 'provision'; Profile = 'Direct' },
            @{ Phase = 'sequential'; Generation = 'current'; Mode = 'old-sql-rejected'; Profile = 'Direct'; ExpectedFailure = $true },
            @{ Phase = 'sequential'; Generation = 'current'; Mode = 'provision'; Profile = 'Direct' }
        )
        foreach ($profile in @('Buffered', 'Direct', 'Bulk')) {
            $sequence += @{ Phase = 'sequential'; Generation = 'previous'; Mode = 'verify'; Profile = $profile }
            $sequence += @{ Phase = 'sequential'; Generation = 'current'; Mode = 'verify'; Profile = $profile }
        }
        foreach ($phase in $sequence) { Invoke-Phase $provider $schema $phase }
    }

    if ($Phases -contains 'Overlap') {
        $schema = "acta_overlap_$runId"
        $overlapDir = Join-Path $artifactRoot "$provider-overlap"
        New-Item -ItemType Directory -Path $overlapDir -Force | Out-Null
        $env:ACTA_SMOKE_OVERLAP_DIR = $overlapDir
        Invoke-Phase $provider $schema @{ Phase = 'overlap'; Generation = 'previous'; Mode = 'provision'; Profile = 'Direct' }

        # The previous worker claims the hold probe while it is alone, and holds it from here on.
        $hold = Start-Phase $provider $schema @{ Phase = 'overlap'; Generation = 'previous'; Mode = 'hold'; Profile = 'Direct' }
        $heldBy = [DateTime]::UtcNow.AddSeconds(60)
        while (-not (Test-Path -LiteralPath (Join-Path $overlapDir 'held'))) {
            if ($hold.Process.HasExited) { Wait-Phase $provider $hold; throw "$($hold.Label) exited before holding its execution." }
            if ([DateTime]::UtcNow -gt $heldBy) { $hold.Process.Kill(); throw "$($hold.Label) never held its execution." }
            Start-Sleep -Milliseconds 200
        }

        # The current generation provisions under the held execution, then both generations run side by
        # side with migrations disabled, each worker free to claim the other's jobs.
        Invoke-Phase $provider $schema @{ Phase = 'overlap'; Generation = 'current'; Mode = 'provision'; Profile = 'Direct' }
        foreach ($profile in @('Buffered', 'Direct', 'Bulk')) {
            $pair = @(
                (Start-Phase $provider $schema @{ Phase = 'overlap'; Generation = 'previous'; Mode = 'overlap'; Profile = $profile }),
                (Start-Phase $provider $schema @{ Phase = 'overlap'; Generation = 'current'; Mode = 'overlap'; Profile = $profile })
            )
            foreach ($started in $pair) { Wait-Phase $provider $started }
        }

        # Only now may the held execution complete, on the current generation's objects.
        New-Item -ItemType File -Path (Join-Path $overlapDir 'release') | Out-Null
        Wait-Phase $provider $hold
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
    Phases = $Phases
    Results = $results
}
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactRoot 'evidence.json') -Encoding utf8
Write-Output "Rolling-upgrade smoke passed. Evidence: $artifactRoot"
