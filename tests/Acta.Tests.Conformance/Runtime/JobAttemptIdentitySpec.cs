using System.Text.Json;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Proves the handler-facing attempt identity is the ledger's, not a default: the probe notes
/// <c>ctx.ExecutionNumber</c> and <c>ctx.WorkerId</c> on every attempt, fails once, and the two notes
/// are compared against the <c>execution_number</c> and <c>worker_id</c> the engine stamped on the
/// matching events.
/// </summary>
[ConformanceSpec(
    "job-context.attempt-identity",
    "A handler reads the attempt number and worker the ledger recorded for it",
    Area = "Execution",
    Contract = "ctx.ExecutionNumber and ctx.WorkerId report the running attempt's ledger identity, advancing with each retry.",
    Arrange = "An attempt-identity probe notes both values per attempt and throws on its first attempt.",
    Act = "The runtime drives the job through its failed first attempt to a successful second.",
    Assert = "Two notes read attempts 1 then 2, each matching the execution number the engine stamped on its own event, and both name the registered worker."
)]
public abstract class JobAttemptIdentitySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "ctx.ExecutionNumber advances with each attempt and ctx.WorkerId matches the executing worker")]
    public async Task Context_reports_the_ledger_attempt_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "attempt-identity", ct);

        // Backoff is 0s, so the re-armed row is claimable on the very next tick; the loop is bounded
        // well above the two attempts this job needs so a wiring regression fails fast rather than hangs.
        for (var i = 0; i < 6 && await Jobs.GetStatusAsync(enqueued, ct) is not JobStatusCode.Succeeded; i++)
        {
            await Runtime.RunOnceAsync(enqueued, ct);
        }

        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));

        var notes = await Operations.Ledger.ListEventsAsync(
            new ListJobEventsQuery(JobId: enqueued.JobId, EventCode: JobEventCode.JobNoteRecorded, PageSize: 50),
            ct
        );
        Assert.Equal(2, notes.Items.Count);

        // Each note carries what the handler read; the event it rides carries what the engine stamped
        // for that same attempt. Comparing them pairwise is the assertion - two matching numbers read
        // from one source would prove nothing.
        var identities = notes
            .Items.Select(e => (Stamped: e.ExecutionNumber, Read: Read(e.DetailText!)))
            .OrderBy(pair => pair.Stamped)
            .ToList();

        Assert.Equal([1, 2], identities.Select(pair => pair.Stamped));
        Assert.All(identities, pair => Assert.Equal(pair.Stamped, pair.Read.ExecutionNumber));

        // The worker the handler named is the one running it, which is what makes the value usable for
        // correlating handler logs with the ledger's worker rows.
        var workers = await Operations.Workers.ListAsync(new ListWorkersQuery(JobNamespace: TestNamespace), ct);
        var worker = Assert.Single(workers.Items);
        Assert.All(identities, pair => Assert.Equal(worker.WorkerId, pair.Read.WorkerId));
    }

    // Note details are written through the framework serializer, whose wire shape is camelCase.
    private static AttemptIdentityProbe.AttemptIdentity Read(string detail) =>
        JsonSerializer.Deserialize<AttemptIdentityProbe.AttemptIdentity>(detail, JsonSerializerOptions.Web)!;
}
