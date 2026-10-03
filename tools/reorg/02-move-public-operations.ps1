# Moves the public operation types in src/Acta under Operations/<Group>/, the public half of the slice
# layout in docs/internals/source-layout.md. A group folder gets what its facade takes and returns and the
# codes those models carry; a type that belongs to one operation and already starts with that operation's
# name goes in the operation's folder. The handler declaration types join Execution/, and the alert
# transport and worker wakeup seams join Hosting/, so no old group folder survives. Acta has one
# namespace, so no C# changes; markdown links to a moved file follow it. Re-running is a no-op.
#
#   pwsh tools/reorg/02-move-public-operations.ps1

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$acta = "$repo/src/Acta"

# Old folder -> destination for every file in it, then the files that go elsewhere.
$folders = [ordered]@{
    'Alerts' = 'Operations/Alerts'
    'Definitions' = 'Operations/Definitions'
    'Events' = 'Operations/Ledger'
    'Jobs' = 'Operations/Jobs'
    'Ledger' = 'Operations/Ledger'
    'Namespaces' = 'Operations/Namespaces'
    'Outbox' = 'Operations/Outbox'
    'Schedules' = 'Operations/Schedules'
    'Settings' = 'Operations/Settings'
    'Tags' = 'Operations/Tags'
    'Tenants' = 'Operations/Tenants'
    'Workers' = 'Operations/Workers'
}
$files = @{
    'Ledger/ListJobsQuery.cs' = 'Operations/Ledger/ListJobs'
    'Ledger/ListEventsQuery.cs' = 'Operations/Ledger/ListEvents'
    'Outbox/ListOutboxSourcesQuery.cs' = 'Operations/Outbox/ListOutboxSources'
    'Workers/ListWorkersQuery.cs' = 'Operations/Workers/ListWorkers'
    'Jobs/ActorCode.cs' = 'Operations/Ledger'
    'Jobs/DeadlineBehaviorCode.cs' = 'Operations/Definitions'
    'Jobs/JobAuditLevelCode.cs' = 'Operations/Definitions'
    'Jobs/JobAttribute.cs' = 'Execution'
    'Jobs/IJobManifest.cs' = 'Execution'
    'Jobs/JobDescriptorManifest.cs' = 'Execution'
    'Jobs/JobHandlerInvokeDelegate.cs' = 'Execution'
    'Jobs/JobInvocationKind.cs' = 'Execution'
    'Jobs/JobPipelineBehavior.cs' = 'Execution'
    'Jobs/JobTenantRequirementCode.cs' = 'Execution'
    'Schedules/JobScheduleAttribute.cs' = 'Execution'
    'Schedules/Cron.cs' = 'Execution'
    'Alerts/IAlertTransport.cs' = 'Hosting'
    'Alerts/AlertTransportKinds.cs' = 'Hosting'
    'Alerts/AlertChannelOptions.cs' = 'Hosting'
    'Alerts/AlertChannelStatusCode.cs' = 'Hosting'
    'Workers/IWorkerWakeup.cs' = 'Hosting'
}

$moves = [ordered]@{}
foreach ($folder in $folders.Keys) {
    if (-not (Test-Path "$acta/$folder")) { continue }
    foreach ($file in Get-ChildItem "$acta/$folder" -File -Filter '*.cs') {
        $from = "$folder/$($file.Name)"
        $to = if ($files.Contains($from)) { $files[$from] } else { $folders[$folder] }
        $moves["src/Acta/$from"] = "src/Acta/$to/$($file.Name)"
    }
}
if ($moves.Count -eq 0) { Write-Host 'Nothing to move.'; return }

Push-Location $repo
try {
    foreach ($from in $moves.Keys) {
        New-Item -ItemType Directory -Force (Split-Path $moves[$from]) | Out-Null
        git mv $from $moves[$from]
        if ($LASTEXITCODE -ne 0) { throw "git mv $from failed" }
    }
    foreach ($folder in $folders.Keys) {
        if ((Test-Path "$acta/$folder") -and -not (Get-ChildItem "$acta/$folder" -Recurse -File)) {
            Remove-Item -Recurse "$acta/$folder"
        }
    }

    # Analyzer exceptions are scoped by path, in sections such as [src/Acta/{Jobs/JobRef.cs,...}].
    $config = "$repo/.editorconfig"
    $before = [IO.File]::ReadAllText($config)
    $text = [regex]::Replace($before, '(?m)^\[src/Acta/.*\]\r?$', {
            param($section)
            $header = $section.Value
            foreach ($from in $moves.Keys) {
                $old = [regex]::Escape($from.Substring('src/Acta/'.Length))
                $header = [regex]::Replace($header, "(?<=[/{,])$old(?=[,}\]])", $moves[$from].Substring('src/Acta/'.Length))
            }
            $header
        })
    if ($text -ne $before) { [IO.File]::WriteAllText($config, $text, [Text.UTF8Encoding]::new($false)) }

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
