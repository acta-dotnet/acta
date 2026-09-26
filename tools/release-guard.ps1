<#
.SYNOPSIS
Proves that a release tag commit differs from the certified code commit by documentation only.

.DESCRIPTION
The certification quartet, the benchmarks, and the coverage run all execute against one code commit
(A). The seals, benchmark pages, coverage page, and release notes are then committed on top of it, and
the tag lands on the last of those commits (B). This guard lists every path in A..B and fails when any
of them lies outside the documentation allowlist, so the tree the tag names is provably the tree that
was certified plus its evidence. Run it before tagging; docs/internals/releasing.md names the step.

.EXAMPLE
tools/release-guard.ps1 -CertifiedCommit 22d74be6 -TagCommit HEAD
#>
param(
    [Parameter(Mandatory = $true)] [string] $CertifiedCommit,
    [string] $TagCommit = 'HEAD'
)

$ErrorActionPreference = 'Stop'

$allowed = @(
    '^docs/certification/',
    '^docs/benchmarks/',
    '^docs/release-notes\.md$',
    '^docs/README\.md$',
    '^docs/internals/releasing\.md$',
    # Prose a reader or a coding agent reads, never compiled or packed into a binary: a correction
    # found after certification lands without moving A. The package README is packed as text only.
    '^docs/.+\.md$',
    '^README\.md$',
    '^llms\.txt$',
    # The release checks themselves, which run against the tree and ship nothing.
    '^tools/release-guard\.ps1$',
    '^tools/site-check\.mjs$',
    '^site/'
)

$changed = git diff --name-only "$CertifiedCommit..$TagCommit"
if ($LASTEXITCODE -ne 0) {
    throw "git diff failed for $CertifiedCommit..$TagCommit"
}

$offending = @($changed | Where-Object { $path = $_; -not ($allowed | Where-Object { $path -match $_ }) })

if ($offending.Count -gt 0) {
    Write-Host "Release guard: $CertifiedCommit..$TagCommit changes paths outside the documentation allowlist:" -ForegroundColor Red
    $offending | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "The certified code commit is $CertifiedCommit; a code, schema, test, tool, or generated-artifact change after it abandons that candidate."
    exit 1
}

Write-Host "Release guard: $CertifiedCommit..$TagCommit is documentation only ($($changed.Count) file(s))." -ForegroundColor Green
$changed | ForEach-Object { Write-Host "  $_" }
exit 0
