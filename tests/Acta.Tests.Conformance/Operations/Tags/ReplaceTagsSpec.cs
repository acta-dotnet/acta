using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tags;

/// <summary>Conformance for the whole-set replace: the target ends with exactly the given tags, and an invalid list changes nothing.</summary>
[ConformanceSpec(
    "replace-tags.whole-set",
    "Replace swaps a target's whole tag set in one write",
    Area = "Tags",
    Contract = "Replace leaves an existing target with exactly the given tags, an empty list clears them, and an invalid list throws before any existing tag changes."
)]
public sealed class ReplaceTagsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITags Tags => Services.GetRequiredService<ITags>();

    private async Task<TagTarget> TenantAsync(string name, CancellationToken ct)
    {
        var key = Unique(name);
        await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct);
        return TagTarget.ForTenant(key);
    }

    [Fact(DisplayName = "Replace drops unlisted tags, keeps listed ones with their new values, and a repeat changes nothing")]
    public async Task Target_ends_with_exactly_the_given_list()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("replace-set", ct);
        await Tags.ReplaceAsync(target, [new TagInput("dropped"), new TagInput("kept", "old")], ct: ct);

        TagInput[] next = [new TagInput("kept", "new"), new TagInput("added", "fresh")];
        Assert.Equal(TagMutationAction.Applied, (await Tags.ReplaceAsync(target, next, ct: ct)).Action);
        Assert.Equal(TagMutationAction.Applied, (await Tags.ReplaceAsync(target, next, ct: ct)).Action);

        var tags = Assert.IsType<GetTagsResponse>(await Tags.GetAsync(target, ct));
        Assert.Equal([new GetTagsItem("added", "fresh"), new GetTagsItem("kept", "new")], tags.Items);
    }

    [Fact(DisplayName = "An empty list clears every tag and a full list of 32 is accepted")]
    public async Task Empty_clears_and_the_cap_is_inclusive()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("replace-bounds", ct);

        var full = Enumerable.Range(0, TagLimits.MaxTagsPerTarget).Select(i => new TagInput($"tag-{i}")).ToArray();
        Assert.Equal(TagMutationAction.Applied, (await Tags.ReplaceAsync(target, full, ct: ct)).Action);
        Assert.Equal(TagLimits.MaxTagsPerTarget, Assert.IsType<GetTagsResponse>(await Tags.GetAsync(target, ct)).Items.Count);

        Assert.Equal(TagMutationAction.Applied, (await Tags.ReplaceAsync(target, [], ct: ct)).Action);
        Assert.Empty(Assert.IsType<GetTagsResponse>(await Tags.GetAsync(target, ct)).Items);
    }

    [Fact(DisplayName = "Tag limits and duplicate normalized names reject before replacing existing state")]
    public async Task Validation_rejects_invalid_replacements_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await TenantAsync("replace-invalid", ct);
        await Tags.ReplaceAsync(target, [new TagInput("kept", "value")], ct: ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Tags.ReplaceAsync(target, [new TagInput("duplicate"), new TagInput("duplicate", "two")], ct: ct).AsTask()
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Tags.ReplaceAsync(
                    target,
                    Enumerable.Range(0, TagLimits.MaxTagsPerTarget + 1).Select(i => new TagInput($"tag-{i}")).ToArray(),
                    ct: ct
                )
                .AsTask()
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Tags.ReplaceAsync(target, [new TagInput("name", new string('v', TagLimits.MaxValueLength + 1))], ct: ct).AsTask()
        );

        var unchanged = Assert.IsType<GetTagsResponse>(await Tags.GetAsync(target, ct));
        Assert.Equal([new GetTagsItem("kept", "value")], unchanged.Items);
    }
}
