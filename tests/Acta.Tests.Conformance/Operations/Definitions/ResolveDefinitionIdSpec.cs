using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Conformance for <c>ResolveDefinitionId</c>: a definition's natural key resolves to its catalog id, and a key no
/// definition carries, a prefix of a registered name included, resolves to <c>null</c>.
/// </summary>
[ConformanceSpec(
    "resolve-definition-id.by-key",
    "ResolveDefinitionId finds a definition's catalog id by key, null for no match",
    Area = "Reads",
    Contract = "ResolveDefinitionId returns the catalog id of the definition a namespace and name identify exactly, and null when no definition carries that key."
)]
public sealed class ResolveDefinitionIdSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A registered name resolves to its definition's catalog id")]
    public async Task A_registered_name_resolves_to_its_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var page = await Operations.Definitions.ListAsync(new ListDefinitionsQuery(TestNamespace, PageSize: 1), ct);
        var expected = Assert.Single(page.Items);

        var resolved = await ResolveAsync(expected.JobNamespace, expected.JobName, ct);

        Assert.Equal(expected.DefinitionId, resolved?.DefinitionId);
    }

    [Fact(DisplayName = "An unknown name, a prefix of a registered name, and a name from another namespace resolve to null")]
    public async Task A_key_no_definition_carries_resolves_to_null()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await ResolveAsync(TestNamespace, "no-such-job", ct));
        Assert.Null(await ResolveAsync(TestNamespace, "add-number", ct));
        Assert.Null(await ResolveAsync($"missing-{TestId}", "add-numbers", ct));
    }

    private ValueTask<ResolveDefinitionIdResponse?> ResolveAsync(string jobNamespace, string jobName, CancellationToken ct) =>
        Services.GetRequiredService<ResolveDefinitionIdHandler>().HandleAsync(new ResolveDefinitionIdQuery(jobNamespace, jobName), ct);
}
