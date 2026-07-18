using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Completeness gate: every candidate contract spec has reviewable, unique metadata.
/// </summary>
public sealed class AttributeCompletenessTests
{
    /// <summary>
    /// Asserts each candidate has a complete, unique [ConformanceSpec].
    /// </summary>
    [Fact]
    public void Every_candidate_has_complete_metadata()
    {
        var candidates = typeof(IConformanceFixture).Assembly.GetTypes().Where(HarnessReflection.IsCandidateContractSpec).ToList();
        var failures = new List<string>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var spec in candidates)
        {
            var a = HarnessReflection.ConformanceSpec(spec);
            if (a is null)
            {
                failures.Add($"{spec.Name}: missing [ConformanceSpec].");
                continue;
            }

            void Require(string field, string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    failures.Add($"{spec.Name}: {field} is empty.");
                }
            }

            Require("Id", a.Id);
            Require("Title", a.Title);
            Require("Area", a.Area);
            Require("Contract", a.Contract);
            Require("Arrange", a.Arrange);
            Require("Act", a.Act);
            Require("Assert", a.Assert);

            if (!string.IsNullOrWhiteSpace(a.Id))
            {
                if (ids.TryGetValue(a.Id, out var other))
                {
                    failures.Add($"{spec.Name}: duplicate Id '{a.Id}' (also {other}).");
                }
                else
                {
                    ids[a.Id] = spec.Name;
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
