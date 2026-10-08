using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Conformance for <c>GetDefinition</c>: a single fully-projected <c>definitions</c> row read by its
/// natural key (namespace and name), and <c>null</c> for a name no definition carries.
/// </summary>
[ConformanceSpec(
    "get-job-definition.by-key",
    "GetDefinition returns one definition by name and null for an unknown name",
    Area = "Reads",
    Contract = "GetDefinition returns the fully-projected definitions row for the supplied namespace and name, and null when none matches."
)]
public sealed class GetDefinitionSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A known name returns the matching definition row")]
    public async Task Returns_definition_for_known_name()
    {
        var ct = TestContext.Current.CancellationToken;

        var page = await Operations.Definitions.ListAsync(new ListDefinitionsQuery(TestNamespace, PageSize: 1), ct);
        var expected = Assert.Single(page.Items);

        var def = await Operations.Definitions.GetAsync(expected.JobNamespace, expected.JobName, ct);

        Assert.NotNull(def);
        Assert.Equal(expected.DefinitionId, def!.DefinitionId);
        Assert.Equal(expected.JobName, def.JobName);
        Assert.Equal(expected.JobNamespace, def.JobNamespace);
        // The detail row carries the full projection the grid omits (definition hash, formats, every
        // policy triple); a populated hash proves the full read, not the slim list projection, ran.
        Assert.False(string.IsNullOrEmpty(def.DefinitionHash));
    }

    [Fact(DisplayName = "An unknown name returns null")]
    public async Task Returns_null_for_unknown_name()
    {
        var ct = TestContext.Current.CancellationToken;

        var def = await Operations.Definitions.GetAsync(TestNamespace, "no-such-job", ct);

        Assert.Null(def);
    }

    [Fact(DisplayName = "Display name and description overrides round-trip through the detail projection")]
    public async Task Display_name_and_description_overrides_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;

        var page = await Operations.Definitions.ListAsync(new ListDefinitionsQuery(TestNamespace, PageSize: 1), ct);
        var definition = Assert.Single(page.Items);

        var before = await Operations.Definitions.GetAsync(definition.JobNamespace, definition.JobName, ct);
        Assert.NotNull(before);

        const string displayName = "Renamed By Operator";
        const string description = "Operator description.";
        await DefinitionTestOps.UpdateOverridesAsync(
            Services,
            definition.JobNamespace,
            definition.JobName,
            before!.Version,
            new DefinitionPolicyOverrides(DisplayName: displayName, Description: description),
            new JobControlActor(ActorCode.Operator, "tester"),
            "detail projection round-trip",
            ct
        );

        // The dashboard save/reload cycle reads back through this exact projection - if it omitted
        // these two columns, a save would look applied but the reload would show them as unset.
        var after = await Operations.Definitions.GetAsync(definition.JobNamespace, definition.JobName, ct);

        Assert.NotNull(after);
        Assert.Equal(displayName, after!.DisplayNameOverride);
        Assert.Equal(displayName, after.DisplayNameEffective);
        Assert.Equal(description, after.DescriptionOverride);
        Assert.Equal(description, after.DescriptionEffective);
    }
}
