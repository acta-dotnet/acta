using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tags;

/// <summary>Conformance for the single-tag remove: delete one name from one target, and treat an absent tag as already done.</summary>
[ConformanceSpec(
    "remove-tags.one-tag",
    "Remove deletes one tag by name and treats a missing tag as done",
    Area = "Tags",
    Contract = "Remove deletes one tag by name from an existing target, leaves its other tags and every other target alone, and applies harmlessly when the tag is absent."
)]
public sealed class RemoveTagsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITags Tags => Services.GetRequiredService<ITags>();

    private async Task<TagTarget> TenantAsync(string name, CancellationToken ct)
    {
        var key = TestKey(name);
        await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct);
        return TagTarget.ForTenant(key);
    }

    [Fact(DisplayName = "Remove deletes the named tag and keeps the rest")]
    public async Task Named_tag_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("remove-one", ct);
        await Tags.ReplaceAsync(target, [new TagInput("drop", "x"), new TagInput("keep", "y")], ct: ct);

        var response = await Tags.RemoveAsync(target, "drop", ct: ct);

        Assert.Equal(TagMutationAction.Applied, response.Action);
        Assert.Equal([new GetTagsItem("keep", "y")], Assert.IsType<GetTagsResponse>(await Tags.GetAsync(target, ct)).Items);
    }

    [Fact(DisplayName = "Removing a tag the target does not carry applies and changes nothing")]
    public async Task Absent_tag_is_harmless()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("remove-absent", ct);
        await Tags.UpsertAsync(target, new TagInput("keep"), ct: ct);

        Assert.Equal(TagMutationAction.Applied, (await Tags.RemoveAsync(target, "never-there", ct: ct)).Action);
        Assert.Equal(TagMutationAction.Applied, (await Tags.RemoveAsync(target, "keep", ct: ct)).Action);
        Assert.Equal(TagMutationAction.Applied, (await Tags.RemoveAsync(target, "keep", ct: ct)).Action);

        Assert.Empty(Assert.IsType<GetTagsResponse>(await Tags.GetAsync(target, ct)).Items);
    }

    [Fact(DisplayName = "Remove reaches only the addressed target")]
    public async Task Remove_is_scoped_to_its_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await TenantAsync("remove-first", ct);
        var second = await TenantAsync("remove-second", ct);
        await Tags.UpsertAsync(first, new TagInput("shared", "1"), ct: ct);
        await Tags.UpsertAsync(second, new TagInput("shared", "2"), ct: ct);

        await Tags.RemoveAsync(first, "shared", ct: ct);

        Assert.Empty(Assert.IsType<GetTagsResponse>(await Tags.GetAsync(first, ct)).Items);
        Assert.Equal([new GetTagsItem("shared", "2")], Assert.IsType<GetTagsResponse>(await Tags.GetAsync(second, ct)).Items);
    }

    [Fact(DisplayName = "A malformed or reserved name throws even when the target is missing")]
    public async Task Name_is_validated_before_the_target_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var missing = TagTarget.ForTenant(TestKey("remove-missing"));

        await Assert.ThrowsAsync<ArgumentException>(async () => await Tags.RemoveAsync(missing, "Not Kebab", ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Tags.RemoveAsync(missing, "sys.internal", ct: ct));
        Assert.Equal(TagMutationAction.NotFound, (await Tags.RemoveAsync(missing, "well-formed", ct: ct)).Action);
    }
}
