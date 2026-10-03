# Turns the shared conformance specs into concrete classes that each provider test project compiles from
# source, with a ConformanceFixture alias naming its fixture. The specs lose their TFixture parameter; the
# bound ones become sealed classes; the one-line provider subclasses, the parity meta-specs and their
# reflection go. Testing/, Sql/ and Contracts/ stay a library. Re-running is a no-op.
#
#   pwsh tools/reorg/05-conformance-shells.ps1
#   dotnet csharpier format tests

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$shared = 'tests/Acta.Tests.Conformance'
$folders = 'DbSession', 'Features', 'Runtime', 'Scenarios'
$providers = [ordered]@{ Postgres = 'PgConformanceFixture'; SqlServer = 'SqlServerConformanceFixture'; Sqlite = 'SqliteConformanceFixture' }
$subclass = [regex]'(?m)^public sealed class \w+ : (?<spec>\w+)<\w+ConformanceFixture>;[ \t]*\r?\n'

function Read-Text($path) { [IO.File]::ReadAllText("$repo/$path") }
function Write-Text($path, $text) {
    $bom = ([IO.File]::ReadAllBytes("$repo/$path") | Select-Object -First 1) -eq 0xEF
    [IO.File]::WriteAllText("$repo/$path", $text, [Text.UTF8Encoding]::new($bom))
}

Push-Location $repo
try {
    $providerFiles = $providers.Keys | ForEach-Object { git ls-files "tests/Acta.Tests.Conformance.$_/*.cs" }
    $bound = @{}
    foreach ($file in $providerFiles) { foreach ($m in $subclass.Matches((Read-Text $file))) { $bound[$m.Groups['spec'].Value] = $true } }
    if ($bound.Count -eq 0) { Write-Host 'Nothing to convert.'; return }

    # The specs: every generic class declared in the spec folders drops its TFixture parameter, the
    # library's generic bases take the alias, and a bound spec becomes a sealed concrete class.
    $specFiles = $folders | ForEach-Object { git ls-files "$shared/$_/*.cs" }
    $generic = $specFiles | ForEach-Object { [regex]::Matches((Read-Text $_), 'class (\w+)<TFixture>') | ForEach-Object { $_.Groups[1].Value } } | Sort-Object -Unique
    $ownGeneric = [regex]"\b($($generic -join '|'))<TFixture>"
    foreach ($file in $specFiles) {
        $before = Read-Text $file
        $text = [regex]::Replace($before, '(?m)^[ \t]*where TFixture : IConformanceFixture, new\(\)[ \t]*\r?\n', '')
        $text = $ownGeneric.Replace($text, '$1')
        $text = [regex]::Replace($text, '\bTFixture\b', 'ConformanceFixture')
        $text = [regex]::Replace($text, '(?m)^public abstract (partial )?class (\w+)\b', {
                param($m)
                if ($bound.ContainsKey($m.Groups[2].Value)) { "public sealed $($m.Groups[1].Value)class $($m.Groups[2].Value)" } else { $m.Value }
            })
        if ($text -ne $before) { Write-Text $file $text }
    }

    # The library stops compiling the spec folders; each provider compiles them, and the shared global usings.
    $library = "$shared/Acta.Tests.Conformance.csproj"
    $text = Read-Text $library
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $remove = "  <ItemGroup>$nl    <!-- The specs compile into each provider test project, against its own ConformanceFixture. -->$nl    <Compile Remove=`"$(($folders | ForEach-Object { "$_/**" }) -join ';')`" />$nl  </ItemGroup>$nl"
    Write-Text $library $text.Replace("</Project>", "$remove</Project>")

    foreach ($provider in $providers.Keys) {
        $project = "tests/Acta.Tests.Conformance.$provider/Acta.Tests.Conformance.$provider.csproj"
        $text = Read-Text $project
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $include = "  <ItemGroup>$nl    <!-- The shared specs, bound to this provider by the ConformanceFixture alias in GlobalUsings.cs. -->$nl"
        $include += "    <Compile Include=`"..\Acta.Tests.Conformance\GlobalUsings.cs`" Link=`"Shared\GlobalUsings.cs`" />$nl"
        foreach ($folder in $folders) {
            $include += "    <Compile Include=`"..\Acta.Tests.Conformance\$folder\**\*.cs`" LinkBase=`"Shared\$folder`" />$nl"
        }
        $include += "  </ItemGroup>$nl"
        Write-Text $project $text.Replace("</Project>", "$include</Project>")

        $usings = "tests/Acta.Tests.Conformance.$provider/GlobalUsings.cs"
        $text = Read-Text $usings
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        Write-Text $usings ($text.TrimEnd() + $nl + "global using ConformanceFixture = Acta.Tests.Conformance.$provider.Testing.$($providers[$provider]);" + $nl)
    }

    # The specs now compile into the provider assemblies, so whatever let the library see internals lets
    # them see internals too.
    foreach ($granting in 'src/Acta/Acta.csproj', 'src/Acta.Testing/Acta.Testing.csproj') {
        $text = Read-Text $granting
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $grant = '    <InternalsVisibleTo Include="Acta.Tests.Conformance" />'
        $more = ($providers.Keys | ForEach-Object { "$nl    <InternalsVisibleTo Include=`"Acta.Tests.Conformance.$_`" />" }) -join ''
        Write-Text $granting $text.Replace($grant, $grant + $more)
    }

    # The one-line subclasses go; a file left with no type goes with them. A provider-specific test that
    # derives from a shared base stops passing it the fixture.
    $sharedBase = [regex]"\b($($generic -join '|'))<\w+ConformanceFixture>"
    foreach ($file in $providerFiles) {
        $before = Read-Text $file
        $text = $sharedBase.Replace($subclass.Replace($before, ''), '$1')
        if ($text -eq $before) { continue }
        if ($text -notmatch '(?m)^\s*(public|internal|file)?\s*(sealed |static |abstract |partial )*(class|record|struct|interface|enum) ') {
            git rm -q $file
        }
        else { Write-Text $file $text }
    }

    # Parity is true by construction now: the meta-specs and the reflection they ran on go.
    foreach ($file in (git ls-files 'tests/Acta.Tests.Conformance.*/Meta/*.cs') + @(
            "$shared/Contracts/ParityMetaSpec.cs",
            "$shared/Contracts/IConformanceMetaSpec.cs",
            "$shared/Contracts/HarnessReflection.cs",
            'tests/Acta.Tests/Conformance/ParityBootstrapGuardTests.cs')) {
        if (Test-Path $file) { git rm -q $file }
    }
}
finally { Pop-Location }
Write-Host "Converted $($bound.Count) specs."
