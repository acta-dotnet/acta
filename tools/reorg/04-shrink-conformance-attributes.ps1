# Strips the Arrange/Act/Assert prose from every [ConformanceSpec] and deletes every [CoversStoreMethod]
# line in the conformance specs. The attribute types and the tests that read them are edited by hand in the
# same commit. Re-running is a no-op.
#
#   pwsh tools/reorg/04-shrink-conformance-attributes.ps1

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$prose = [regex]'(?m)^[ \t]*(Arrange|Act|Assert) = "(?:[^"\\]|\\.)*",?[ \t]*\r?\n'
$covers = [regex]'(?m)^[ \t]*\[CoversStoreMethod\([^\r\n]*\)\][ \t]*\r?\n'
# The last argument left in the list must not keep its comma.
$trailing = [regex]'(?m),([ \t]*\r?\n[ \t]*\)\])'

$changed = 0
foreach ($path in git -C $repo ls-files 'tests/*.cs') {
    $full = "$repo/$path"
    $bytes = [IO.File]::ReadAllBytes($full)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF
    $before = [IO.File]::ReadAllText($full)
    if ($before -notmatch '\[ConformanceSpec\(|\[CoversStoreMethod\(') { continue }
    $text = $covers.Replace($prose.Replace($before, ''), '')
    $text = [regex]::Replace($text, '(?s)\[ConformanceSpec\(.*?\)\]', { param($m) $trailing.Replace($m.Value, '$1') })
    if ($text -ne $before) {
        [IO.File]::WriteAllText($full, $text, [Text.UTF8Encoding]::new($bom))
        $changed++
    }
}
Write-Host "Shrank the conformance attributes in $changed files."
