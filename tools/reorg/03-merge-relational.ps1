# Merges Acta.Relational into Acta.Runtime: database access to Db/, table models and the schema to
# Schema/, the relational stores to a transitional Stores/ (they dissolve into handlers in Phase 7), the
# provider option checks to Hosting/. The Acta.Relational namespace becomes Acta.Runtime in every tracked
# C# file outside demos, and in the schema snapshot's foreign-key signatures; .editorconfig sections and
# tracked markdown links follow the files. Project files are edited by hand in the same commit.
#
#   pwsh tools/reorg/03-merge-relational.ps1
#   dotnet csharpier format .

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if (-not (Test-Path "$repo/src/Acta.Relational")) { Write-Host 'Nothing to merge.'; return }

$folders = [ordered]@{
    'Commands' = 'Db'
    'Connections' = 'Db'
    'Resources' = 'Db'
    'Entities' = 'Schema'
    'Schema' = 'Schema'
    'Stores' = 'Stores'
    'Outbox' = 'Stores'
    '.' = 'Stores'
}
$files = @{
    'Connections/SqlProviderOptionsValidator.cs' = 'Hosting'
    'Connections/DriverVersionPreflight.cs' = 'Hosting'
}

Push-Location $repo
try {
    $moves = [ordered]@{}
    foreach ($tracked in git ls-files src/Acta.Relational) {
        $relative = $tracked.Substring('src/Acta.Relational/'.Length)
        if ($relative -eq 'Acta.Relational.csproj') { continue }
        $folder = if ($relative.Contains('/')) { $relative.Substring(0, $relative.LastIndexOf('/')) } else { '.' }
        $to = if ($files.Contains($relative)) { $files[$relative] } else { $folders[$folder] }
        if (-not $to) { throw "No destination for $tracked" }
        $moves[$tracked] = "src/Acta.Runtime/$to/$(Split-Path $relative -Leaf)"
    }
    foreach ($from in $moves.Keys) {
        New-Item -ItemType Directory -Force (Split-Path $moves[$from]) | Out-Null
        git mv $from $moves[$from]
        if ($LASTEXITCODE -ne 0) { throw "git mv $from failed" }
    }
    git rm -q src/Acta.Relational/Acta.Relational.csproj
    Remove-Item -Recurse -Force src/Acta.Relational

    # Repo paths, with or without src/, to a file or a folder: the two Hosting files first, then each
    # folder's destination.
    $paths = [ordered]@{}
    foreach ($relative in $files.Keys) { $paths["Acta.Relational/$relative"] = "Acta.Runtime/$($files[$relative])/$(Split-Path $relative -Leaf)" }
    foreach ($folder in $folders.Keys) { if ($folder -ne '.') { $paths["Acta.Relational/$folder/"] = "Acta.Runtime/$($folders[$folder])/" } }
    foreach ($from in $moves.Keys) { if ($from -notmatch '^src/Acta\.Relational/[^/]+/') { $paths[$from.Substring(4)] = $moves[$from].Substring(4) } }
    function Update-Paths([string]$text) {
        foreach ($from in $paths.Keys) { $text = $text.Replace($from, $paths[$from]) }
        $text
    }

    # The namespace. The snapshot writes its foreign-key arrow as a JSON unicode escape, right before the
    # target type name. A using of the file's own namespace goes, and duplicates collapse to the first,
    # in the header only.
    $qualified = [regex]'(?<=^|[^\w.]|\\u003E)Acta\.Relational(?![\w])'
    $using = [regex]'^\s*(global\s+)?using\s+([\w.]+)\s*;\s*$'
    $targets = git ls-files '*.cs' | Where-Object { $_ -notmatch '^demos/' }
    $targets += 'src/Acta.Runtime/Schema/schema-snapshot.json'
    foreach ($path in $targets) {
        $full = "$repo/$path"
        $bytes = [IO.File]::ReadAllBytes($full)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $before = [IO.File]::ReadAllText($full)
        $text = $qualified.Replace((Update-Paths $before), 'Acta.Runtime')
        if ($path.EndsWith('.cs') -and $text -ne $before) {
            $own = [regex]::Match($text, '(?m)^namespace\s+([\w.]+)\s*;').Groups[1].Value
            $seen = @{}
            $header = $true
            $kept = foreach ($line in $text -split '(?<=\n)') {
                $trim = $line.Trim()
                $header = $header -and ($trim -eq '' -or $trim -match '^(//(?!/)|#|namespace\s|(global\s+)?using\s)')
                $u = $using.Match($line)
                if ($header -and $u.Success) {
                    $key = $u.Groups[1].Value.Trim() + '|' + $u.Groups[2].Value
                    if ($u.Groups[2].Value -eq $own -or $seen.ContainsKey($key)) { continue }
                    $seen[$key] = $true
                }
                $line
            }
            $text = -join $kept
        }
        if ($text -ne $before) { [IO.File]::WriteAllText($full, $text, [Text.UTF8Encoding]::new($bom)) }
    }

    # The Lock table model now shares WorkerWakeup's namespace and would silently replace the
    # System.Threading.Lock its gate means.
    $wakeup = "$repo/src/Acta.Runtime/Modules/Execution/Workers/WorkerWakeup.cs"
    $text = [IO.File]::ReadAllText($wakeup)
    [IO.File]::WriteAllText($wakeup, $text.Replace('private readonly Lock _gate', 'private readonly System.Threading.Lock _gate'), [Text.UTF8Encoding]::new($false))

    # Path-scoped analyzer sections and tracked markdown links.
    foreach ($doc in @('.editorconfig') + (git ls-files '*.md' | Where-Object { $_ -notmatch '^(demos|docs/certification)/' })) {
        $full = "$repo/$doc"
        $bytes = [IO.File]::ReadAllBytes($full)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $before = [IO.File]::ReadAllText($full)
        $text = Update-Paths $before
        if ($text -ne $before) { [IO.File]::WriteAllText($full, $text, [Text.UTF8Encoding]::new($bom)) }
    }
}
finally { Pop-Location }
Write-Host "Moved $($moves.Count) files into src/Acta.Runtime."
