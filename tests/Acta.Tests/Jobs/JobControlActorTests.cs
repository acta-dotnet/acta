using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The actor is the operator's name in the audit event's details: it must survive intact, so the only
/// transformations are whitespace-to-null and a pair-safe cut to the details cap.
/// </summary>
public class JobControlActorTests
{
    [Fact]
    public void A_non_ascii_operator_name_is_kept_intact()
    {
        Assert.Equal("Žiga Novak", JobControlActor.Operator("Žiga Novak"));
    }

    [Fact]
    public void Null_or_whitespace_means_unknown_and_stores_as_null()
    {
        Assert.Null(JobControlActor.Operator(null));
        Assert.Null(JobControlActor.Operator("   "));
    }

    [Fact]
    public void An_actor_at_the_cap_is_kept_whole()
    {
        Assert.Equal(128, JobControlActor.Operator(new string('a', 128))!.Length);
    }

    [Fact]
    public void An_actor_is_display_text_so_a_control_character_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => JobControlActor.Operator("marko\nadmin"));
        Assert.Throws<ArgumentException>(() => JobControlActor.Operator("marko\tadmin"));
    }

    [Fact]
    public void An_operator_name_past_the_cap_is_cut_rather_than_rejected()
    {
        Assert.Equal(128, JobControlActor.Operator(new string('a', 300))!.Length);
    }

    [Fact]
    public void The_operator_lands_in_the_event_details_as_actor()
    {
        var details = EventDetails.Write(("actor", JobControlActor.Operator("Žiga Novak")));

        Assert.Equal("Žiga Novak", EventDetails.Read(JobPayloadFormat.Json.Id, details, "actor"));
    }
}
