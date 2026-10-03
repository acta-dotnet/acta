# Moves every provider's SQL out of src/Acta.<Provider>/Sql/ into src/Acta.Runtime, beside the operation it
# belongs to, as X.<dialect>.sql: Operations/<Group>/<Stem>/<Stem>.pg.sql, the schema commands to Schema/,
# the operator views to Schema/Views/, and the ApplyTags routine the three tag slices share to the Tags
# group folder until Phase 10 splits it. The group comes from the old folder, with the stems the operation
# map assigns elsewhere listed below. The .routine and .view infixes go: the catalog tells a routine by its
# header and a view by its name. Ordered migrations stay in the providers. Markdown links to a moved file
# follow it, and the layout ratchet drops the provider Sql/ entries. Re-running is a no-op.
#
#   pwsh tools/reorg/06-move-sql.ps1

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$providers = [ordered]@{ 'Acta.Postgres' = 'pg'; 'Acta.SqlServer' = 'mssql'; 'Acta.Sqlite' = 'sqlite' }

# Old folder under Sql/ -> operation group.
$groups = @{
    'Alerting' = 'Alerts'
    'Execution' = 'Execution'
    'Execution/Checkpoints' = 'Execution'
    'Execution/ChildLatches' = 'Execution'
    'Execution/Definitions' = 'Definitions'
    'Execution/Jobs' = 'Jobs'
    'Execution/Namespaces' = 'Namespaces'
    'Execution/Notes' = 'Execution'
    'Execution/Schedules' = 'Schedules'
    'Execution/Settings' = 'Settings'
    'Execution/Signals' = 'Signals'
    'Execution/Tenants' = 'Tenants'
    'Execution/Timers' = 'Execution'
    'Execution/Workers' = 'Workers'
    'Maintenance' = 'Maintenance'
    'Operations/Events' = 'Ledger'
    'Operations/Overview' = 'Ledger'
    'Operations/Tags' = 'Tags'
    'Outbox' = 'Outbox'
    'Services/Locks' = 'Locks'
    'Services/Time' = 'Clock'
}
# Stems whose group is not their old folder's.
$stems = @{
    'ListJobs' = 'Ledger'
    'ConsumeOutboxSignal' = 'Outbox'
    'GetOutboxSignal' = 'Outbox'
    'ParkOutboxSignal' = 'Outbox'
    'RecordOutboxEvent' = 'Outbox'
}

$moves = [ordered]@{}
foreach ($project in $providers.Keys) {
    $token = $providers[$project]
    $root = "$repo/src/$project/Sql"
    if (-not (Test-Path $root)) { continue }
    foreach ($file in Get-ChildItem $root -Recurse -File -Filter '*.sql') {
        $folder = [IO.Path]::GetRelativePath($root, $file.DirectoryName).Replace('\', '/')
        $stem = $file.Name.Split('.')[0]
        $to =
            if ($stem.EndsWith('View')) { "Schema/Views/$stem.$token.sql" }
            elseif ($folder -eq 'Schema') { "Schema/$stem.$token.sql" }
            elseif ($stem -eq 'ApplyTags') { "Operations/Tags/$stem.$token.sql" }
            else {
                $group = if ($stems.Contains($stem)) { $stems[$stem] } elseif ($groups.Contains($folder)) { $groups[$folder] } else { throw "No group for Sql/$folder/$($file.Name)" }
                "Operations/$group/$stem/$stem.$token.sql"
            }
        $moves["src/$project/Sql/$folder/$($file.Name)".Replace('/./', '/')] = "src/Acta.Runtime/$to"
    }
}
if ($moves.Count -eq 0) { Write-Host 'Nothing to move.'; return }
$targets = $moves.Values | Group-Object | Where-Object Count -gt 1
if ($targets) { throw "Two files move to $($targets[0].Name)" }

Push-Location $repo
try {
    foreach ($from in $moves.Keys) {
        New-Item -ItemType Directory -Force (Split-Path $moves[$from]) | Out-Null
        git mv $from $moves[$from]
        if ($LASTEXITCODE -ne 0) { throw "git mv $from failed" }
    }
    foreach ($project in $providers.Keys) {
        $root = "$repo/src/$project/Sql"
        if ((Test-Path $root) -and -not (Get-ChildItem $root -Recurse -File)) { Remove-Item -Recurse $root }
    }

    $ratchet = "$repo/tests/ratchets/layout.txt"
    $before = [IO.File]::ReadAllText($ratchet)
    $text = [regex]::Replace($before, '(?m)^sql-placement: src/Acta\.(Postgres|SqlServer|Sqlite)/Sql\r?\n', '')
    if ($text -ne $before) { [IO.File]::WriteAllText($ratchet, $text, [Text.UTF8Encoding]::new($false)) }

    $links = 0
    # Tracked documents only, demos aside; the certification records keep the paths they were sealed with.
    $markdown = git ls-files '*.md' | Where-Object { $_ -notmatch '^(demos|docs/certification)/' } | ForEach-Object { Get-Item "$repo/$_" }
    foreach ($doc in $markdown) {
        $bytes = [IO.File]::ReadAllBytes($doc.FullName)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $before = [IO.File]::ReadAllText($doc.FullName)
        $text = $before
        foreach ($from in $moves.Keys) { $text = $text.Replace($from, $moves[$from]) }
        if ($text -ne $before) {
            [IO.File]::WriteAllText($doc.FullName, $text, [Text.UTF8Encoding]::new($bom))
            $links++
        }
    }
}
finally { Pop-Location }
Write-Host "Moved $($moves.Count) files; updated links in $links documents."
