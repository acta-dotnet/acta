[CmdletBinding(DefaultParameterSetName = 'Compare')]
param(
    [Parameter(ParameterSetName = 'Compare', Position = 0)]
    [string]$Resource,

    [Parameter(ParameterSetName = 'Compare')]
    [ValidateSet('pg', 'mssql', 'sqlite')]
    [string[]]$Provider = @('pg', 'mssql', 'sqlite'),

    [Parameter(ParameterSetName = 'Inventory', Mandatory)]
    [switch]$List,

    [Parameter(ParameterSetName = 'Changed', Mandatory)]
    [string]$ChangedSince
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$providerProjects = [ordered]@{
    pg = 'Acta.Postgres'
    mssql = 'Acta.SqlServer'
    sqlite = 'Acta.Sqlite'
}

# A SQL file is named by its stem, the part before the first dot: PauseJob for PauseJob.pg.sql.
function ConvertTo-LogicalResource([string]$relativePath) {
    return (Split-Path -Leaf $relativePath.Replace('\', '/')).Split('.')[0]
}

function Get-ProviderInventory([string]$token) {
    $runtimeRoot = Join-Path $repoRoot 'src/Acta.Runtime'
    $inventory = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($owner in @('Operations', 'Schema')) {
        $ownerRoot = Join-Path $runtimeRoot $owner
        if (-not (Test-Path -LiteralPath $ownerRoot)) { continue }

        foreach ($file in Get-ChildItem -LiteralPath $ownerRoot -Recurse -File -Filter "*.$token.sql") {
            $logical = ConvertTo-LogicalResource $file.Name
            if ($inventory.ContainsKey($logical)) {
                throw "Provider '$token' has duplicate logical SQL resource '$logical': '$($inventory[$logical])' and '$($file.FullName)'."
            }
            $inventory.Add($logical, $file.FullName)
        }
    }
    return $inventory
}

$inventories = @{}
foreach ($token in $providerProjects.Keys) {
    $inventories[$token] = Get-ProviderInventory $token
}

if ($List) {
    $all = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($inventory in $inventories.Values) {
        foreach ($logical in $inventory.Keys) { [void]$all.Add($logical) }
    }

    foreach ($logical in @($all) | Sort-Object) {
        $owners = foreach ($token in $providerProjects.Keys) {
            if ($inventories[$token].ContainsKey($logical)) { $token }
        }
        "{0,-60} {1}" -f $logical, ($owners -join ',')
    }
    exit 0
}

if ($ChangedSince) {
    $range = "$ChangedSince...HEAD"
    $changedFiles = @(& git -C $repoRoot diff --name-only $range -- src/Acta.Runtime/Operations src/Acta.Runtime/Schema)
    if ($LASTEXITCODE -ne 0) { throw "git diff failed for '$range'." }

    $changed = @{}
    foreach ($path in $changedFiles) {
        if (-not $path.EndsWith('.sql', [StringComparison]::OrdinalIgnoreCase)) { continue }

        $token = $null
        foreach ($candidate in $providerProjects.Keys) {
            if ($path.EndsWith(".$candidate.sql", [StringComparison]::OrdinalIgnoreCase)) {
                $token = $candidate
                break
            }
        }
        if ($null -eq $token) { continue }

        $logical = ConvertTo-LogicalResource $path
        if (-not $changed.ContainsKey($logical)) {
            $changed[$logical] = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        }
        [void]$changed[$logical].Add($token)
    }

    if ($changed.Count -eq 0) {
        Write-Output 'No SQL files changed.'
        exit 0
    }

    Write-Output '# Provider SQL changed-sibling report'
    Write-Output ''
    Write-Output "Compared changes since ``$ChangedSince`` (range ``$range``). A changed resource should prompt review of every sibling, even when a dialect-specific edit leaves it untouched."
    Write-Output ''
    Write-Output '| SQL stem | PostgreSQL | SQL Server | SQLite |'
    Write-Output '| --- | --- | --- | --- |'
    foreach ($logical in $changed.Keys | Sort-Object) {
        $cells = foreach ($token in $providerProjects.Keys) {
            if (-not $inventories[$token].ContainsKey($logical)) {
                'missing'
                continue
            }
            $relativePath = [IO.Path]::GetRelativePath($repoRoot, $inventories[$token][$logical]).Replace('\', '/')
            $state = if ($changed[$logical].Contains($token)) { '**changed**' } else { 'unchanged' }
            "$state · ``$relativePath``"
        }
        Write-Output "| ``$logical`` | $($cells[0]) | $($cells[1]) | $($cells[2]) |"
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Resource)) {
    throw 'Specify a SQL stem such as EnqueueOne, or use -List / -ChangedSince.'
}

$logicalResource = ConvertTo-LogicalResource $Resource.Trim()
$resolved = @()
foreach ($token in $Provider) {
    if (-not $inventories[$token].ContainsKey($logicalResource)) {
        throw "Provider '$token' has no SQL resource '$logicalResource'. Run tools/sql-compare.ps1 -List to see every stem."
    }
    $path = $inventories[$token][$logicalResource]
    $resolved += [pscustomobject]@{
        Token = $token
        Path = $path
        RelativePath = [IO.Path]::GetRelativePath($repoRoot, $path).Replace('\', '/')
    }
}

Write-Output "SQL stem: $logicalResource"
foreach ($item in $resolved) {
    Write-Output ("  {0,-6} {1}" -f $item.Token, $item.RelativePath)
}

if ($resolved.Count -eq 1) {
    Get-Content -LiteralPath $resolved[0].Path
    exit 0
}

for ($left = 0; $left -lt $resolved.Count - 1; $left++) {
    for ($right = $left + 1; $right -lt $resolved.Count; $right++) {
        Write-Output ''
        Write-Output "### $($resolved[$left].Token) vs $($resolved[$right].Token)"
        & git -C $repoRoot diff --no-index --no-ext-diff -- $resolved[$left].RelativePath $resolved[$right].RelativePath
        if ($LASTEXITCODE -gt 1) { throw 'git diff --no-index failed.' }
    }
}
exit 0
