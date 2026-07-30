using Acta.Modules.Execution.Definitions;
using Acta.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Conformance for <c>GetJobDefinition</c>: a single fully-projected <c>definitions</c> row read by
/// surrogate id (the same projection/mapper as the definitions list), and <c>null</c> for an unknown id.
/// </summary>
[ConformanceSpec(
    "get-job-definition.by-id",
    "GetJobDefinition returns one definition by id and null for an unknown id",
    Area = "Reads",
    Contract = "GetJobDefinition returns the fully-projected definitions row matching the supplied id and null when no row matches.",
    Arrange = "A definition is registered in the test namespace and its id is known from a list read.",
    Act = "GetJobDefinition is called with the known id and then with an id that matches no row.",
    Assert = "The known id returns the fully-projected definition row matching the list read and the unknown id returns null."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.GetDefinitionAsync))]
public abstract class GetJobDefinitionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A known id returns the matching definition row")]
    public async Task Returns_definition_for_known_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var page_all = await Services
            .GetRequiredService<IDefinitionStore>()
            .ListDefinitionsAsync(new DefinitionPageRequest(TestNamespace, null, null, null, null, null, 1, false), ct);
        var (all, _) = (page_all.Rows, page_all.Total);
        Assert.NotEmpty(all);
        var expected = all[0];

        var def = await Services.GetRequiredService<IDefinitionStore>().GetDefinitionAsync(expected.JobDefinitionId, ct);

        Assert.NotNull(def);
        Assert.Equal(expected.JobDefinitionId, def!.JobDefinitionId);
        Assert.Equal(expected.JobName, def.JobName);
        Assert.Equal(expected.JobNamespace, def.JobNamespace);
        // The detail row carries the full projection the grid omits (definition hash, formats, every
        // policy triple); a populated hash proves the full read, not the slim list projection, ran.
        Assert.False(string.IsNullOrEmpty(def.DefinitionHash));
    }

    [Fact(DisplayName = "An unknown id returns null")]
    public async Task Returns_null_for_unknown_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var def = await Services.GetRequiredService<IDefinitionStore>().GetDefinitionAsync(int.MaxValue, ct);

        Assert.Null(def);
    }

    [Fact(DisplayName = "Display name and description overrides round-trip through the detail projection")]
    public async Task Display_name_and_description_overrides_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;

        var page_all = await Services
            .GetRequiredService<IDefinitionStore>()
            .ListDefinitionsAsync(new DefinitionPageRequest(TestNamespace, null, null, null, null, null, 1, false), ct);
        var (all, _) = (page_all.Rows, page_all.Total);
        Assert.NotEmpty(all);
        var definitionId = all[0].JobDefinitionId;

        var before = await Services.GetRequiredService<IDefinitionStore>().GetDefinitionAsync(definitionId, ct);
        Assert.NotNull(before);

        const string displayName = "Renamed By Operator";
        const string description = "Operator description.";
        await DefinitionTestOps.SetOverridesAsync(
            Services,
            definitionId,
            before!.Version,
            new JobDefinitionPolicyOverrides(DisplayName: displayName, Description: description),
            new JobControlActor(JobActorCode.Operator, "tester"),
            "detail projection round-trip",
            ct
        );

        // The dashboard save/reload cycle reads back through this exact projection - if it omitted
        // these two columns, a save would look applied but the reload would show them as unset.
        var after = await Services.GetRequiredService<IDefinitionStore>().GetDefinitionAsync(definitionId, ct);

        Assert.NotNull(after);
        Assert.Equal(displayName, after!.DisplayNameOverride);
        Assert.Equal(displayName, after.DisplayNameEffective);
        Assert.Equal(description, after.DescriptionOverride);
        Assert.Equal(description, after.DescriptionEffective);
    }
}
