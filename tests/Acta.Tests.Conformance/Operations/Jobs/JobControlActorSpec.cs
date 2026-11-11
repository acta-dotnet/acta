using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for who a job verb records: the operator it was given is its event's <c>actor</c> column, kept exactly
/// as supplied, and the details keep only the machine-readable facts.
/// </summary>
[ConformanceSpec(
    "control.actor",
    "A job verb records its operator as the event's actor",
    Area = "Control",
    Contract = "The operator a job verb names is its event's actor, byte-identical on every provider, and the event's details name no actor."
)]
[Deploys("add-numbers")]
public sealed class JobControlActorSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<JobControlActorSpec>>
{
    // Two-byte UTF-8 letters: a varchar column or an ASCII parameter would fold them.
    private const string Operator = "Jože Kovač";

    [Theory(DisplayName = "The operator who applied a job verb is the event's actor, and the details name no actor")]
    [InlineData("pause", EventCode.JobPaused)]
    [InlineData("resume", EventCode.JobResumed)]
    [InlineData("cancel", EventCode.JobCancelled)]
    [InlineData("restart", EventCode.JobRestarted)]
    [InlineData("reschedule", EventCode.JobRescheduled)]
    [InlineData("reprioritize", EventCode.JobReprioritized)]
    [InlineData("update-input", EventCode.JobInputAmended)]
    [InlineData("purge", EventCode.JobPurged)]
    public async Task Operator_is_the_events_actor(string verb, EventCode recorded)
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);
        if (verb == "resume")
        {
            await JobStateStaging.SetStatusAsync(Db, job.JobId, JobStatusCode.Paused, ct);
        }
        else if (verb is "restart" or "purge")
        {
            await JobStateStaging.SetStatusAsync(Db, job.JobId, JobStatusCode.Succeeded, ct);
        }

        var action = verb switch
        {
            "pause" => (await Jobs.PauseAsync(job, "why", Operator, ct: ct)).Action,
            "resume" => (await Jobs.ResumeAsync(job, "why", Operator, ct: ct)).Action,
            "cancel" => (await Jobs.CancelAsync(job, "why", Operator, ct: ct)).Action,
            "restart" => (await Jobs.RestartAsync(job, "why", Operator, ct: ct)).Action,
            "reschedule" => (await Jobs.RescheduleAsync(job, DateTime.UtcNow.AddHours(1), "why", Operator, ct: ct)).Action,
            "reprioritize" => (await Jobs.ReprioritizeAsync(job, JobPriorityCode.High, "why", Operator, ct: ct)).Action,
            "update-input" => (await Jobs.UpdateInputAsync(job, JobPayload.Json(new AddNumbers(4, 5)), "why", Operator, ct)).Action,
            _ => (await Jobs.PurgeAsync(job, "why", Operator, ct)).Action,
        };

        Assert.Equal(ControlAction.Applied, action);
        var evt = await ReadSingleEventAsync(job.JobId, recorded, ct);
        Assert.Equal(Operator, evt.Actor);
        Assert.Null(EventDetail(evt, "actor"));
    }
}
