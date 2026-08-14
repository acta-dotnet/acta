using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using Acta.AspNetCore.Web;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The QueryBinding reads in a handler and its QueryParameterDoc declarations are two hand-written
/// lists describing one contract, and the OpenAPI gate cannot see when they diverge: a doc with no
/// binding freezes a phantom filter (a client sends it and is silently unfiltered), and a binding
/// with no doc never reaches the document at all. This scan holds the two lists equal per endpoint
/// in <c>ActaApiEndpoints.cs</c>, expanding shared blocks through reflection so the pins can never
/// lag the blocks themselves.
/// </summary>
public sealed partial class QueryDocCoverageTests
{
    [GeneratedRegex("Query,\\s*\"(?<name>[A-Za-z]+)\"|Query,\\s*\\r?\\n\\s*\"(?<name>[A-Za-z]+)\"", RegexOptions.Singleline)]
    private static partial Regex QueryReads();

    [GeneratedRegex("new QueryParameterDoc\\(\\s*\"(?<name>[A-Za-z]+)\"", RegexOptions.Singleline)]
    private static partial Regex DocDeclarations();

    [GeneratedRegex(@"\.\.\s*QueryParameterDocExtensions\.(?<block>\w+)")]
    private static partial Regex BlockSpreads();

    [GeneratedRegex(@"""(?<route>/[^""]*)""")]
    private static partial Regex FirstRoute();

    [Fact]
    public void Every_endpoint_documents_exactly_the_query_keys_its_handler_reads()
    {
        var source = File.ReadAllText(
            Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.AspNetCore", "Web", "ActaApiEndpoints.cs")
        );
        var blocks = typeof(QueryParameterDocExtensions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ImmutableArray<QueryParameterDoc>))
            .ToDictionary(f => f.Name, f => ((ImmutableArray<QueryParameterDoc>)f.GetValue(null)!).Select(d => d.Name).ToArray());

        var failures = new List<string>();
        var chunks = Regex.Split(source, @"\.Map(?:Get|Post|Put|Delete|Patch)\(");
        foreach (var chunk in chunks.Skip(1))
        {
            var route = FirstRoute().Match(chunk).Groups["route"].Value;

            var reads = QueryReads().Matches(chunk).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
            if (chunk.Contains("QueryBinding.Tags(", StringComparison.Ordinal))
            {
                reads.Add("tag");
            }

            var declared = DocDeclarations().Matches(chunk).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match spread in BlockSpreads().Matches(chunk))
            {
                if (!blocks.TryGetValue(spread.Groups["block"].Value, out var names))
                {
                    failures.Add($"{route}: unknown shared block '{spread.Groups["block"].Value}'.");
                    continue;
                }
                declared.UnionWith(names);
            }

            foreach (var phantom in declared.Except(reads).Order(StringComparer.Ordinal))
            {
                failures.Add($"{route}: documents query parameter '{phantom}' the handler never reads.");
            }
            foreach (var undocumented in reads.Except(declared).Order(StringComparer.Ordinal))
            {
                failures.Add($"{route}: reads query parameter '{undocumented}' without documenting it.");
            }
        }

        Assert.True(failures.Count == 0, "Query docs and handler reads diverged:\n" + string.Join('\n', failures));
    }
}
