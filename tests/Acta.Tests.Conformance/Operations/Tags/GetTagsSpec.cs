using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tags;

/// <summary>Conformance for the tag read: one target's tags, exact values in name order, and null for a target that does not exist.</summary>
[ConformanceSpec(
    "get-tags.read",
    "Get reads one target's tags and null for a missing target",
    Area = "Tags",
    Contract = "Get returns an existing target's tags with their exact values in name order, an empty set when it has none, and null when the target is missing."
)]
public sealed class GetTagsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITags Tags => Services.GetRequiredService<ITags>();

    private async Task<TagTarget> TenantAsync(string name, CancellationToken ct)
    {
        var key = TestKey(name);
        await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct);
        return TagTarget.ForTenant(key);
    }

    [Fact(DisplayName = "A target without tags reads an empty set and a missing target reads null")]
    public async Task Empty_and_missing_are_distinct()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("get-empty", ct);

        var tags = await Tags.GetAsync(target, ct);
        Assert.NotNull(tags);
        Assert.Empty(tags.Items);

        Assert.Null(await Tags.GetAsync(TagTarget.ForTenant(TestKey("get-missing")), ct));
    }

    [Fact(DisplayName = "Each tag reads back its exact value, and a presence-only tag reads a null value")]
    public async Task Values_read_back_exactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("get-values", ct);
        await Tags.ReplaceAsync(target, [new TagInput("region", "EU West / Ljubljana"), new TagInput("flagged")], ct: ct);

        var tags = Assert.IsType<GetTagsResponse>(await Tags.GetAsync(target, ct));

        Assert.Equal([new GetTagsItem("flagged"), new GetTagsItem("region", "EU West / Ljubljana")], tags.Items);
    }

    [Fact(DisplayName = "A tenant key or job name addressed in another case reads the same target")]
    public async Task Target_addresses_fold_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantKey = TestKey("get-case");
        await Services.GetRequiredService<ITenants>().RegisterAsync(tenantKey, null, null, ct);
        await Tags.UpsertAsync(TagTarget.ForTenant(tenantKey), new TagInput("tier", "gold"), ct: ct);

        var jobName = TestKey("get-case-job");
        await Seeder.SeedJobDefinitionAsync(TestNamespaceId, name: jobName, ct: ct);
        await Tags.UpsertAsync(TagTarget.ForDefinition(TestNamespace, jobName), new TagInput("owner", "billing"), ct: ct);

        var tenant = Assert.IsType<GetTagsResponse>(await Tags.GetAsync(TagTarget.ForTenant(tenantKey.ToUpperInvariant()), ct));
        Assert.Equal([new GetTagsItem("tier", "gold")], tenant.Items);
        var definition = Assert.IsType<GetTagsResponse>(
            await Tags.GetAsync(TagTarget.ForDefinition(TestNamespace, jobName.ToUpperInvariant()), ct)
        );
        Assert.Equal([new GetTagsItem("owner", "billing")], definition.Items);
    }

    [Fact(DisplayName = "A read returns only the addressed target's tags")]
    public async Task Read_is_scoped_to_its_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await TenantAsync("get-first", ct);
        var second = await TenantAsync("get-second", ct);
        await Tags.UpsertAsync(first, new TagInput("only-first"), ct: ct);
        await Tags.UpsertAsync(second, new TagInput("only-second"), ct: ct);

        Assert.Equal([new GetTagsItem("only-first")], Assert.IsType<GetTagsResponse>(await Tags.GetAsync(first, ct)).Items);
        Assert.Equal([new GetTagsItem("only-second")], Assert.IsType<GetTagsResponse>(await Tags.GetAsync(second, ct)).Items);
    }
}
