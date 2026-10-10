using Acta.Emit.Features.Docs;
using Acta.Emit.Shared.Model;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class CodeFamilyEmitterTests
{
    // The persisted vocabulary sits in one folder; JobPayloadFormat stays with the payload API.
    [Fact]
    public void Every_code_family_is_declared_in_the_codes_folder()
    {
        var codes = Path.Combine(ResolveRepoRoot(), "src", "Acta", "Domain", "Codes");
        var misplaced = CodeFamilyDiscovery
            .DiscoverAll(SchemaModel.Discover())
            .Select(family => family.Name)
            .Where(name => !File.Exists(Path.Combine(codes, name + ".cs")));

        Assert.Empty(misplaced);
    }

    [Fact]
    public void Code_family_inventory_is_sorted_by_family_name()
    {
        var markdown = CodeFamilyEmitter.EmitCodes(SchemaModel.Discover());
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var inventoryStart = Array.IndexOf(lines, "## Code-family inventory");
        Assert.True(inventoryStart >= 0);

        var names = lines.Skip(inventoryStart + 4).TakeWhile(line => line.StartsWith('|')).Select(line => line.Split('`')[1]).ToArray();

        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    [Fact]
    public void Code_family_reference_contains_no_editorial_metadata()
    {
        var markdown = CodeFamilyEmitter.EmitCodes(SchemaModel.Discover());

        Assert.DoesNotContain("| Style |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| Set by |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("At a glance", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Capacity", markdown, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Acta.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate Acta.slnx from " + AppContext.BaseDirectory);
    }
}
