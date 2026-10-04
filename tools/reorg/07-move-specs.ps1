# Moves the conformance specs so the tree mirrors src/Acta.Runtime: Operations/<Group>/ for an operation's specs,
# Engine/<Area>/ for the engine's, Db/ and Schema/, with Scenarios/ kept for the end-to-end specs. The map is
# 07-move-specs.csv (old_path,new_path,old_class,new_class,reason, relative to tests/Acta.Tests.Conformance; a blank
# new_path deletes the file).
# Each moved file takes the namespace of its new folder; a renamed class is renamed everywhere under tests/; every
# test file drops the usings of namespaces that no longer exist and gains one for each moved type it names; the
# provider shells compile the new top folders; markdown links follow the files. Re-running is a no-op.
#
#   pwsh tools/reorg/07-move-specs.ps1

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Set-Location $repo
$specs = 'tests/Acta.Tests.Conformance'
$all = Import-Csv "$PSScriptRoot/07-move-specs.csv"
foreach ($row in $all | Where-Object { -not $_.new_path }) {
    if (Test-Path "$specs/$($row.old_path)") { git rm -q "$specs/$($row.old_path)" }
}
$rows = $all | Where-Object { $_.new_path }
$tops = $rows | ForEach-Object { $_.new_path.Split('/')[0] } | Sort-Object -Unique

function Read([string]$path) { [IO.File]::ReadAllText($path) }
function Save([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text) }
function Namespace([string]$relative) {
    $folder = [IO.Path]::GetDirectoryName($relative).Replace('\', '/').Replace('/', '.')
    "Acta.Tests.Conformance.$folder"
}
$nsLine = [regex]'(?m)^namespace\s+([\w.]+)\s*;'
$typeDecl = [regex]'(?m)^(?:public |internal )?(?:abstract |sealed |static |partial )*(?:class|record|struct|interface|enum)\s+(\w+)'

# 1. Move, and give each moved file its folder's namespace.
$movedTypes = @{}
$oldNamespaces = [Collections.Generic.HashSet[string]]::new()
$newNamespaces = [Collections.Generic.HashSet[string]]::new()
foreach ($row in $rows) {
    $old = "$specs/$($row.old_path)"; $new = "$specs/$($row.new_path)"
    if ((Test-Path $old) -and $old -ne $new) {
        New-Item -ItemType Directory -Force (Split-Path $new) | Out-Null
        git mv $old $new
    }
    $text = Read $new
    $current = $nsLine.Match($text).Groups[1].Value
    $target = Namespace $row.new_path
    [void]$oldNamespaces.Add($current); [void]$newNamespaces.Add($target)
    if ($current -ne $target) { Save $new ($nsLine.Replace($text, "namespace $target;", 1)) }
    foreach ($m in $typeDecl.Matches((Read $new))) { $movedTypes[$m.Groups[1].Value] = $target }
}

# 2. Renamed classes, everywhere under tests/.
$renames = $rows | Where-Object { $_.old_class -ne $_.new_class }
$testFiles = git ls-files 'tests/*.cs'
foreach ($file in $testFiles) {
    $text = Read $file; $orig = $text
    foreach ($r in $renames) { $text = [regex]::Replace($text, "\b$([regex]::Escape($r.old_class))\b", $r.new_class) }
    if ($text -ne $orig) { Save $file $text }
}
foreach ($r in $renames) {
    if ($movedTypes.ContainsKey($r.old_class)) { $movedTypes[$r.new_class] = $movedTypes[$r.old_class]; $movedTypes.Remove($r.old_class) }
}

# 3. Usings: drop the dissolved namespaces, add one per moved type a file names from another namespace.
$gone = $oldNamespaces | Where-Object { -not $newNamespaces.Contains($_) }
foreach ($file in $testFiles) {
    if (-not (Test-Path $file)) { continue }
    $text = Read $file; $orig = $text
    foreach ($ns in $gone) { $text = [regex]::Replace($text, "(?m)^using $([regex]::Escape($ns));\r?\n", '') }
    $own = $nsLine.Match($text).Groups[1].Value
    # Only files compiled beside the specs (the spec folders and the provider shells) can see a spec namespace.
    $beside = $file -match "^$([regex]::Escape($specs))\.\w+/" -or ($file.StartsWith("$specs/") -and $tops -contains $file.Substring($specs.Length + 1).Split('/')[0])
    $needed = $movedTypes.GetEnumerator() | Where-Object { $beside } |
        Where-Object { $_.Value -ne $own -and $text -match "\b$($_.Key)\b" -and $text -notmatch "(?m)^using $([regex]::Escape($_.Value));" } |
        ForEach-Object { $_.Value } | Sort-Object -Unique
    if ($needed) {
        $usings = ($needed | ForEach-Object { "using $_;" }) -join "`n"
        $last = [regex]::Matches($text, '(?m)^using [\w.]+;\r?\n') | Select-Object -Last 1
        $text = if ($last) { $text.Insert($last.Index + $last.Length, "$usings`n") } else { "$usings`n`n$text" }
    }
    if ($text -ne $orig) { Save $file $text }
}

# 4. The provider shells compile the new top folders; the library leaves them to the shells.
foreach ($csproj in (git ls-files 'tests/Acta.Tests.Conformance.*/*.csproj')) {
    $text = Read $csproj
    $lines = ($tops | ForEach-Object { "    <Compile Include=`"..\Acta.Tests.Conformance\$_\**\*.cs`" LinkBase=`"Shared\$_`" />" }) -join "`r`n"
    $text = [regex]::Replace($text, '(?m)^    <Compile Include="\.\.\\Acta\.Tests\.Conformance\\(?!GlobalUsings)[^"]+\\\*\*\\\*\.cs" LinkBase="[^"]+" />\r?\n', '')
    $text = [regex]::Replace($text, '(?m)^(    <Compile Include="\.\.\\Acta\.Tests\.Conformance\\GlobalUsings\.cs"[^\r\n]*\r?\n)', "`$1$lines`r`n")
    Save $csproj $text
}
$library = "$specs/Acta.Tests.Conformance.csproj"
Save $library ([regex]::Replace((Read $library), '<Compile Remove="[^"]*" />', "<Compile Remove=`"$(($tops | ForEach-Object { "$_/**" }) -join ';')`" />"))

# 5. Markdown links and .editorconfig sections follow the files.
foreach ($md in @(git ls-files '*.md') + '.editorconfig') {
    $text = Read $md; $orig = $text
    foreach ($row in $rows | Where-Object { $_.old_path -ne $_.new_path }) {
        $text = $text.Replace("Acta.Tests.Conformance/$($row.old_path)", "Acta.Tests.Conformance/$($row.new_path)")
    }
    if ($text -ne $orig) { Save $md $text }
}
"moved $(@($rows | Where-Object { $_.old_path -ne $_.new_path }).Count) files, renamed $(@($renames).Count) classes"
