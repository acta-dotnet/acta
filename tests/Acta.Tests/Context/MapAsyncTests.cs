using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// MapAsync sugar: requires a stable item key, rejects duplicate keys, derives deterministic
/// group-scoped child names (readable for safe keys, hashed otherwise), starts every child before
/// waiting, and returns outcomes keyed back to the original items.
/// </summary>
public sealed class MapAsyncTests
{
    private sealed record Image(int Id, string Url);

    private sealed record ResizeInput(int Id);

    private static readonly Image[] ThreeImages = [new(1, "a.png"), new(2, "b.png"), new(3, "c.png")];

    [Fact]
    public async Task Map_returns_outcomes_keyed_to_items_in_order()
    {
        var ctx = new RecordingJobContext();

        var resized = await ctx.MapAsync(
            "resize-images",
            ThreeImages,
            itemKey: img => img.Id,
            child: img => new ResizeInput(img.Id),
            ct: TestContext.Current.CancellationToken
        );

        Assert.Equal("resize-images", resized.GroupName);
        Assert.Equal(new[] { 1, 2, 3 }, resized.Items.Select(i => i.Key));
        Assert.All(resized.Items, i => Assert.True(i.Outcome.Succeeded));
        Assert.Equal(resized.Items.Select(i => i.ChildJobId), resized.Items.Select(i => i.Outcome.ChildJobId));
    }

    [Fact]
    public async Task Map_derives_readable_names_for_safe_keys()
    {
        var ctx = new RecordingJobContext();

        await ctx.MapAsync(
            "resize-images",
            ThreeImages,
            img => img.Id,
            img => new ResizeInput(img.Id),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new[] { "resize-images-1", "resize-images-2", "resize-images-3" }, ctx.Started.Select(s => s.Name));
    }

    [Fact]
    public async Task Map_waits_for_all_children()
    {
        var ctx = new RecordingJobContext();

        await ctx.MapAsync(
            "resize-images",
            ThreeImages,
            img => img.Id,
            img => new ResizeInput(img.Id),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(3, ctx.Events.Count(e => e.StartsWith("wait:")));
    }

    [Fact]
    public async Task Map_starts_all_children_before_waiting()
    {
        var ctx = new RecordingJobContext();

        await ctx.MapAsync(
            "resize-images",
            ThreeImages,
            img => img.Id,
            img => new ResizeInput(img.Id),
            TestContext.Current.CancellationToken
        );

        var firstWait = ctx.Events.FindIndex(e => e.StartsWith("wait:"));
        var lastStart = ctx.Events.FindLastIndex(e => e.StartsWith("start:"));
        Assert.True(lastStart < firstWait, "every child must start before any wait");
    }

    [Fact]
    public async Task Map_rejects_duplicate_keys_before_enqueueing()
    {
        var ctx = new RecordingJobContext();
        var dupes = new[] { new Image(1, "a.png"), new Image(1, "b.png") };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            ctx.MapAsync("resize-images", dupes, img => img.Id, img => new ResizeInput(img.Id), TestContext.Current.CancellationToken)
        );

        Assert.Empty(ctx.Started);
    }

    [Fact]
    public async Task Map_rejects_null_item_key()
    {
        var ctx = new RecordingJobContext();
        var items = new[] { "a", "b" };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            ctx.MapAsync("group", items, _ => (string)null!, _ => new ResizeInput(0), TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Map_hashes_unsafe_keys_deterministically()
    {
        var unsafeKey = new[] { "Has Space/And.Caps" };

        var ctx1 = new RecordingJobContext();
        await ctx1.MapAsync("group", unsafeKey, s => s, _ => new ResizeInput(0), TestContext.Current.CancellationToken);

        var ctx2 = new RecordingJobContext();
        await ctx2.MapAsync("group", unsafeKey, s => s, _ => new ResizeInput(0), TestContext.Current.CancellationToken);

        var name = ctx1.Started[0].Name;
        Assert.Equal(name, ctx2.Started[0].Name);
        Assert.StartsWith("group-", name);
        Assert.DoesNotContain(' ', name);
        Assert.True(IdentifierSyntax.IsKebab(name), $"derived name '{name}' must be kebab");
    }

    [Fact]
    public async Task Map_returns_failed_items_and_escalates_on_demand()
    {
        var ctx = new RecordingJobContext(new Dictionary<string, ChildJobOutcome> { ["resize-images-2"] = new(0, JobStatusCode.Failed) });

        var resized = await ctx.MapAsync(
            "resize-images",
            ThreeImages,
            img => img.Id,
            img => new ResizeInput(img.Id),
            TestContext.Current.CancellationToken
        );

        Assert.False(resized.Succeeded);
        Assert.Equal(2, Assert.Single(resized.Failed).Key);
        Assert.Throws<ChildGroupException>(resized.ThrowIfAnyFailed);
    }
}
