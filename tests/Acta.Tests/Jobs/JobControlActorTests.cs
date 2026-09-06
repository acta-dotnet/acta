using Acta.Runtime.Modules.Execution.Api;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The actor id is the operator's name on the audit trail: it must survive intact, so the only
/// transformations are whitespace-to-null and the caller's pair-safe cut to the column.
/// </summary>
public class JobControlActorTests
{
    [Fact]
    public void A_non_ascii_operator_name_is_kept_intact()
    {
        var actor = new JobControlActor(ActorCode.Operator, "Žiga Novak");

        Assert.Equal("Žiga Novak", actor.ActorKey);
    }

    [Fact]
    public void Null_or_whitespace_means_unknown_and_stores_as_null()
    {
        Assert.Null(new JobControlActor(ActorCode.Operator, null).ActorKey);
        Assert.Null(new JobControlActor(ActorCode.Operator, "   ").ActorKey);
    }

    [Fact]
    public void An_over_length_actor_key_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new JobControlActor(ActorCode.Operator, new string('a', 129)));
        Assert.Equal(128, new JobControlActor(ActorCode.Operator, new string('a', 128)).ActorKey!.Length);
    }
}
