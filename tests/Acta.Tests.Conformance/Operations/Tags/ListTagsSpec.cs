using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tags;

/// <summary>
/// Conformance for the tag list: one target's tags paged by name in ordinal order, exact values intact, and an empty
/// page for a target without tags or one that does not exist.
/// </summary>
[ConformanceSpec(
    "list-tags.page",
    "ListTags pages one target's tags by name in ordinal order",
    Area = "Tags",
    Contract = "ListTags pages a target's tags with exact values by name in ordinal order, empty when it has none or is missing, and refuses a foreign cursor."
)]
public sealed class ListTagsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITags Tags => Services.GetRequiredService<ITags>();

    private async Task<TagTarget> TenantAsync(string name, CancellationToken ct)
    {
        var key = Unique(name);
        await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct);
        return TagTarget.ForTenant(key);
    }

    [Fact(DisplayName = "A target without tags and a target that does not exist both list an empty page")]
    public async Task Untagged_and_missing_targets_list_an_empty_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var untagged = await TenantAsync("list-empty", ct);

        foreach (
            var target in new[]
            {
                untagged,
                TagTarget.ForTenant(Unique("list-missing")),
                TagTarget.ForNamespace(Unique("list-missing")),
                TagTarget.ForDefinition(TestNamespace, Unique("list-missing")),
                TagTarget.ForJob(JobLookup.ById(long.MaxValue)),
            }
        )
        {
            var page = await Tags.ListAsync(new ListTagsQuery(target, IncludeTotal: true), ct);

            Assert.Empty(page.Items);
            Assert.False(page.HasMore);
            Assert.Null(page.NextCursor);
            Assert.Equal(0, page.TotalCount);
        }
    }

    [Fact(DisplayName = "Each tag reads back its exact value, and a presence-only tag reads a null value")]
    public async Task Values_read_back_exactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("list-values", ct);
        await Tags.ReplaceAsync(target, [new TagInput("region", "EU West / Ljubljana"), new TagInput("flagged")], ct: ct);

        var tags = await Tags.ListAsync(new ListTagsQuery(target), ct);

        Assert.Equal([new ListTagsItem("flagged"), new ListTagsItem("region", "EU West / Ljubljana")], tags.Items);
    }

    [Fact(DisplayName = "A tenant name or job name addressed in another case reads the same target")]
    public async Task Target_addresses_fold_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantName = Unique("list-case");
        await Services.GetRequiredService<ITenants>().RegisterAsync(tenantName, null, null, ct);
        await Tags.UpsertAsync(TagTarget.ForTenant(tenantName), new TagInput("tier", "gold"), ct: ct);

        var jobName = Unique("list-case-job");
        await Seeder.SeedJobDefinitionAsync(TestNamespaceId, name: jobName, ct: ct);
        await Tags.UpsertAsync(TagTarget.ForDefinition(TestNamespace, jobName), new TagInput("owner", "billing"), ct: ct);

        var tenant = await Tags.ListAsync(new ListTagsQuery(TagTarget.ForTenant(tenantName.ToUpperInvariant())), ct);
        Assert.Equal([new ListTagsItem("tier", "gold")], tenant.Items);
        var definition = await Tags.ListAsync(new ListTagsQuery(TagTarget.ForDefinition(TestNamespace, jobName.ToUpperInvariant())), ct);
        Assert.Equal([new ListTagsItem("owner", "billing")], definition.Items);
    }

    [Fact(DisplayName = "A read returns only the addressed target's tags")]
    public async Task Read_is_scoped_to_its_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await TenantAsync("list-first", ct);
        var second = await TenantAsync("list-second", ct);
        await Tags.UpsertAsync(first, new TagInput("only-first"), ct: ct);
        await Tags.UpsertAsync(second, new TagInput("only-second"), ct: ct);

        Assert.Equal([new ListTagsItem("only-first")], (await Tags.ListAsync(new ListTagsQuery(first), ct)).Items);
        Assert.Equal([new ListTagsItem("only-second")], (await Tags.ListAsync(new ListTagsQuery(second), ct)).Items);
    }

    /// <summary>
    /// Ordinal order puts '-' before '.' before the digits before the letters, and a prefix before its extensions; a
    /// linguistic collation would sort a-a after a9. A page boundary falls between each of them, so the keyset must
    /// compare exactly as the page orders or a tag is skipped or read twice.
    /// </summary>
    [Fact(DisplayName = "Pages walk to the end in ordinal order across hyphen, dot, and digit boundaries, each tag once")]
    public async Task Pages_walk_in_ordinal_order_to_the_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("list-pages", ct);
        string[] names = ["z-last", "a9", "a10", "a1", "a0", "a.b", "a-first", "a-a"];
        await Tags.ReplaceAsync(target, [.. names.Select(name => new TagInput(name))], ct: ct);

        var seen = new List<string>();
        var query = new ListTagsQuery(target, PageSize: 3, IncludeTotal: true);
        for (var pages = 0; ; pages++)
        {
            Assert.True(pages < 4, "the cursor did not reach the end in four pages");
            var page = await Tags.ListAsync(query, ct);
            Assert.Equal(3, page.PageSize);
            Assert.Equal(names.Length, page.TotalCount);
            seen.AddRange(page.Items.Select(item => item.Name));
            if (page.NextCursor is null)
            {
                Assert.False(page.HasMore);
                break;
            }

            Assert.True(page.HasMore);
            Assert.Equal(3, page.Items.Count);
            query = query with { Cursor = page.NextCursor };
        }

        Assert.Equal(["a-a", "a-first", "a.b", "a0", "a1", "a10", "a9", "z-last"], seen);
    }

    [Fact(DisplayName = "A cursor another target or another list handed out is refused, and so is a malformed one")]
    public async Task Foreign_and_malformed_cursors_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await TenantAsync("list-cursor-first", ct);
        var second = await TenantAsync("list-cursor-second", ct);
        await Tags.ReplaceAsync(first, [new TagInput("one"), new TagInput("two")], ct: ct);
        await Tags.ReplaceAsync(second, [new TagInput("one"), new TagInput("two")], ct: ct);

        var cursor = (await Tags.ListAsync(new ListTagsQuery(first, PageSize: 1), ct)).NextCursor;
        Assert.NotNull(cursor);
        var tenants = await Services.GetRequiredService<ITenants>().ListAsync(new ListTenantsQuery(PageSize: 1), ct);
        Assert.NotNull(tenants.NextCursor);

        await Assert.ThrowsAsync<InvalidPageCursorException>(async () =>
            await Tags.ListAsync(new ListTagsQuery(second, PageSize: 1, Cursor: cursor), ct)
        );
        await Assert.ThrowsAsync<InvalidPageCursorException>(async () =>
            await Tags.ListAsync(new ListTagsQuery(first, PageSize: 1, Cursor: tenants.NextCursor), ct)
        );
        await Assert.ThrowsAsync<InvalidPageCursorException>(async () =>
            await Tags.ListAsync(new ListTagsQuery(first, Cursor: "not-a-cursor"), ct)
        );
    }
}
