using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The in-process side of the rolling-deploy exclusion, no DB: a worker that bounces a definition for
/// want of a handler stops claiming it, and the set it binds into the claim is a snapshot rather than
/// a live view of the dictionary.
/// </summary>
public sealed class UnsupportedDefinitionExclusionTests
{
    private static WorkerContext NewContext() => new(workerRegistration: null);

    [Fact]
    public void A_claim_request_filters_no_definition_by_default()
    {
        var request = new ClaimRequest(NamespaceId: 1, WorkerId: 2, MaxBatch: 8);

        Assert.Null(request.ExcludedDefinitionIds);
    }

    [Fact]
    public void A_fresh_context_excludes_nothing()
    {
        Assert.Empty(NewContext().UnsupportedDefinitionIdsSnapshot);
    }

    [Fact]
    public void Excluding_a_definition_answers_true_only_for_the_first_add()
    {
        var context = NewContext();

        // The first add is what the bounce warns on; every later one is the rest of a claimed batch.
        Assert.True(context.ExcludeDefinition(7));
        Assert.False(context.ExcludeDefinition(7));
        Assert.Equal([7], context.UnsupportedDefinitionIdsSnapshot);
    }

    [Fact]
    public void Every_add_publishes_a_new_snapshot_carrying_the_whole_set()
    {
        var context = NewContext();

        context.ExcludeDefinition(7);
        var afterFirst = context.UnsupportedDefinitionIdsSnapshot;
        context.ExcludeDefinition(9);
        var afterSecond = context.UnsupportedDefinitionIdsSnapshot;

        Assert.NotSame(afterFirst, afterSecond);
        Assert.Equal([7], afterFirst);
        Assert.Equal([7, 9], [.. afterSecond.Order()]);
    }

    [Fact]
    public void Reads_between_changes_return_the_same_array()
    {
        var context = NewContext();
        context.ExcludeDefinition(7);

        Assert.Same(context.UnsupportedDefinitionIdsSnapshot, context.UnsupportedDefinitionIdsSnapshot);
    }

    [Fact]
    public void Forgetting_a_definition_drops_it_from_the_snapshot()
    {
        var context = NewContext();
        context.ExcludeDefinition(7);
        context.ExcludeDefinition(9);

        Assert.True(context.ForgetUnsupportedDefinition(7));
        Assert.False(context.ForgetUnsupportedDefinition(7));
        Assert.Equal([9], context.UnsupportedDefinitionIdsSnapshot);
    }
}
