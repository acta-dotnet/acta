#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Line and branch coverage for the unit suite plus the SQLite conformance suite, merged into one
  report. Visibility only - this script never fails on a percentage.

.DESCRIPTION
  Two legs, one report:

    tests/Acta.Tests                       the provider-neutral unit suite
    tests/Acta.Tests.Conformance.Sqlite    the conformance specs against a real ledger

  Both run with tests/Acta.coverage.runsettings, which carries no database connection strings, so
  the leg needs no container and measures the same surface on a laptop and on the CI runner.
  coverlet.collector writes one Cobertura file per leg; ReportGenerator merges them and emits the
  merged Cobertura, an HTML report, and a text summary.

  There is no threshold and no gate on purpose. A coverage target invites gaming - tests written to
  colour lines rather than to falsify behaviour - and the number would then certify the gaming. The
  deliverable of this leg is the blind-spot list in docs/certification/coverage-baseline-rc1.md,
  which names the concurrency and failure paths nothing exercises. Read the list, not the percentage.

.PARAMETER Configuration
  Build configuration to test. Release by default, matching CI.

.PARAMETER NoBuild
  Skip build and restore. Use when the solution was just built (CI does; it passes -NoBuild).

.PARAMETER OutputDirectory
  Where the merged report lands. artifacts/coverage by default (artifacts/ is git-ignored).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts/coverage'
}

$rawDirectory = Join-Path $OutputDirectory 'raw'
$settings = Join-Path $repoRoot 'tests/Acta.coverage.runsettings'

if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $rawDirectory | Out-Null

$projects = @(
    'tests/Acta.Tests/Acta.Tests.csproj',
    'tests/Acta.Tests.Conformance.Sqlite/Acta.Tests.Conformance.Sqlite.csproj'
)

foreach ($project in $projects) {
    Write-Host ""
    Write-Host "== coverage leg: $project ==" -ForegroundColor Cyan

    $arguments = @(
        'test'
        (Join-Path $repoRoot $project)
        '-c', $Configuration
        '--settings', $settings
        '--collect:XPlat Code Coverage'
        '--results-directory', $rawDirectory
    )
    if ($NoBuild) { $arguments += '--no-build' }

    dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Coverage leg failed: $project (exit $LASTEXITCODE). The tests must pass before the numbers mean anything."
    }
}

$reports = Get-ChildItem -Path $rawDirectory -Recurse -Filter 'coverage.cobertura.xml'
if ($reports.Count -lt $projects.Count) {
    throw "Expected $($projects.Count) Cobertura files under $rawDirectory, found $($reports.Count). Is coverlet.collector referenced by both test projects?"
}

Write-Host ""
Write-Host "== merging $($reports.Count) Cobertura reports ==" -ForegroundColor Cyan

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed (exit $LASTEXITCODE)." }

# HtmlInline_AzurePipelines is the single-file-per-page HTML that survives being downloaded as a
# CI artifact and opened from disk; MarkdownSummaryGithub is what the step summary renders.
dotnet reportgenerator `
    "-reports:$(Join-Path $rawDirectory '**/coverage.cobertura.xml')" `
    "-targetdir:$OutputDirectory" `
    '-reporttypes:Cobertura;HtmlInline_AzurePipelines;TextSummary;MarkdownSummaryGithub' `
    '-title:Acta coverage (unit + SQLite conformance)' `
    '-verbosity:Warning'
if ($LASTEXITCODE -ne 0) { throw "ReportGenerator failed (exit $LASTEXITCODE)." }

$summaryFile = Join-Path $OutputDirectory 'Summary.txt'
if (Test-Path $summaryFile) {
    Write-Host ""
    Get-Content $summaryFile | Write-Host
}

Write-Host ""
Write-Host "Merged report: $OutputDirectory" -ForegroundColor Green
Write-Host "No threshold is applied. The deliverable is docs/certification/coverage-baseline-rc1.md."
