using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tags;

/// <summary>Conformance for the single-tag upsert: insert or overwrite one name, leave the rest, and hold the per-target cap.</summary>
[ConformanceSpec(
    "upsert-tags.one-tag",
    "Upsert inserts or overwrites one tag and leaves the others alone",
    Area = "Tags",
    Contract = "Upsert inserts one tag or overwrites its value on an existing target, leaves the other tags untouched, and refuses a new name past the 32-tag cap."
)]
public sealed class UpsertTagsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITags Tags => Services.GetRequiredService<ITags>();

    private async Task<TagTarget> TenantAsync(string name, CancellationToken ct)
    {
        var key = TestKey(name);
        await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct);
        return TagTarget.ForTenant(key);
    }

    [Fact(DisplayName = "Upserting a new name adds it beside the existing tags")]
    public async Task New_name_is_added()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("upsert-add", ct);
        await Tags.ReplaceAsync(target, [new TagInput("kept", "yes")], ct: ct);

        var response = await Tags.UpsertAsync(target, new TagInput("added", "now"), ct: ct);

        Assert.Equal(TagMutationAction.Applied, response.Action);
        var tags = Assert.IsType<TagSet>(await Tags.GetAsync(target, ct));
        Assert.Equal([new TagItem("added", "now"), new TagItem("kept", "yes")], tags.Items);
    }

    [Fact(DisplayName = "Upserting an existing name overwrites its value, and repeating the write changes nothing")]
    public async Task Existing_name_is_overwritten_and_repeat_is_harmless()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("upsert-over", ct);
        await Tags.UpsertAsync(target, new TagInput("stage", "draft"), ct: ct);

        Assert.Equal(TagMutationAction.Applied, (await Tags.UpsertAsync(target, new TagInput("stage", "final"), ct: ct)).Action);
        Assert.Equal(TagMutationAction.Applied, (await Tags.UpsertAsync(target, new TagInput("stage", "final"), ct: ct)).Action);

        var tags = Assert.IsType<TagSet>(await Tags.GetAsync(target, ct));
        Assert.Equal([new TagItem("stage", "final")], tags.Items);

        // A null value is a value too: the upsert clears the old one rather than keeping it.
        await Tags.UpsertAsync(target, new TagInput("stage"), ct: ct);
        Assert.Equal([new TagItem("stage")], Assert.IsType<TagSet>(await Tags.GetAsync(target, ct)).Items);
    }

    [Fact(DisplayName = "At the cap a new name is refused with nothing written, while an existing name still updates")]
    public async Task Cap_refuses_a_new_name_but_not_an_overwrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("upsert-cap", ct);
        await Tags.ReplaceAsync(
            target,
            Enumerable.Range(0, TagLimits.MaxTagsPerTarget).Select(i => new TagInput($"tag-{i}")).ToArray(),
            ct: ct
        );

        // The cap is enforced by the write itself, so it surfaces as the provider's error.
        await Assert.ThrowsAnyAsync<DbException>(async () => await Tags.UpsertAsync(target, new TagInput("one-too-many"), ct: ct));
        var full = Assert.IsType<TagSet>(await Tags.GetAsync(target, ct));
        Assert.Equal(TagLimits.MaxTagsPerTarget, full.Count);
        Assert.DoesNotContain(full.Items, t => t.Name == "one-too-many");

        Assert.Equal(TagMutationAction.Applied, (await Tags.UpsertAsync(target, new TagInput("tag-0", "updated"), ct: ct)).Action);
        Assert.Contains(new TagItem("tag-0", "updated"), Assert.IsType<TagSet>(await Tags.GetAsync(target, ct)).Items);
    }

    [Fact(DisplayName = "A malformed name, a reserved sys name, or an oversize value throws before any write")]
    public async Task Invalid_tags_throw_before_any_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("upsert-invalid", ct);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Tags.UpsertAsync(target, new TagInput("Not Kebab"), ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Tags.UpsertAsync(target, new TagInput("sys.internal"), ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Tags.UpsertAsync(target, new TagInput("long", new string('v', TagLimits.MaxValueLength + 1)), ct: ct)
        );

        Assert.Empty(Assert.IsType<TagSet>(await Tags.GetAsync(target, ct)));
    }
}
