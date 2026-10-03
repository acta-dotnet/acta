# Flattens every src project to one namespace, its assembly root. The sub-namespaces come from the
# declarations in src; every C# file in the repo (demos excepted: they build against the packages) then
# has its declarations, usings, and qualified names rewritten to the root. Re-running is a no-op.
#
#   pwsh tools/reorg/01-flatten-namespaces.ps1
#   dotnet csharpier format .

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path

function Get-SourceFiles([string]$dir, [string]$filter) {
    Get-ChildItem $dir -Recurse -File -Filter $filter |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
}

# Sub-namespace -> project root, read from the declarations in each project.
$map = @{}
foreach ($project in Get-ChildItem "$repo/src" -Directory) {
    $root = $project.Name
    foreach ($file in Get-SourceFiles $project.FullName '*.cs') {
        foreach ($m in [regex]::Matches((Get-Content $file.FullName -Raw), '(?m)^namespace\s+([\w.]+)')) {
            $ns = $m.Groups[1].Value
            if ($ns.StartsWith("$root.")) { $map[$ns] = $root }
        }
    }
}
if ($map.Count -eq 0) { Write-Host 'Nothing to flatten.'; return }

# Longest first, so a nested namespace wins over its parent. The dashboard's manifest resource prefix
# (Acta.AspNetCore.Web.Assets) is a file path, not a namespace, and stays. The schema snapshot writes
# its foreign-key arrow as a JSON unicode escape, right before the target type name.
$names = $map.Keys | Sort-Object Length -Descending
$qualified = [regex]"(?<=^|[^\w.]|\\u003E)(global::)?($(($names | ForEach-Object { [regex]::Escape($_) }) -join '|'))(?![\w])(?!\.Assets\b)"
$using = [regex]'^\s*(global\s+)?using\s+([\w.]+)\s*;\s*$'

$targets = @('src', 'tests', 'anvil', 'tools', 'concepts', 'support') |
    ForEach-Object { Get-SourceFiles "$repo/$_" '*.cs' }
$targets += Get-Item "$repo/src/Acta.Relational/Schema/schema-snapshot.json"
$targets += Get-Item "$repo/docs/guide/production.md", "$repo/docs/guide/testing.md"

$changed = 0
foreach ($file in $targets) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $before = [Text.Encoding]::UTF8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
    $text = $qualified.Replace($before, { param($m) $m.Groups[1].Value + $map[$m.Groups[2].Value] })

    if ($file.Extension -eq '.cs') {
        # A file-scoped namespace needs no using of itself; a file with no declaration (GlobalUsings.cs)
        # owns its project's root. A block namespace owns nothing: top-level statements beside it still
        # need the using. Duplicate usings collapse to the first. Only the header is touched, so a using
        # inside a test's source snippet stays.
        $own = [regex]::Match($text, '(?m)^namespace\s+([\w.]+)\s*;').Groups[1].Value
        if (-not $own -and $text -notmatch '(?m)^namespace\s' -and $file.FullName.StartsWith("$repo\src\")) {
            $own = $file.FullName.Substring("$repo\src\".Length).Split('\')[0]
        }
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

    if ($file.Extension -eq '.cs' -and $text.Trim() -eq '' -and $before.Trim() -ne '') {
        # A GlobalUsings.cs that only imported its own project's sub-namespaces.
        Remove-Item $file.FullName
        $changed++
    }
    elseif ($text -ne $before) {
        [IO.File]::WriteAllText($file.FullName, $text, [Text.UTF8Encoding]::new($bom))
        $changed++
    }
}
Write-Host "Flattened $($map.Count) namespaces across $changed files."
