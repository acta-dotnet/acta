using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the applied-command evidence event: written against the sys.outbox slot job with
/// the operator actor, always emitted regardless of the job's event recording, because it is the only
/// trail for actions whose subject rows live (or lived) in the producer's database.
/// </summary>
[ConformanceSpec(
    "outbox-signal.evidence",
    "An applied operator command leaves an always-emitted evidence event",
    Area = "Outbox",
    Contract = "RecordApplied appends the outbox event against the slot job with the operator actor and reason evidence, even when the job records no other events."
)]
public sealed class RecordOutboxEventSpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "The evidence event lands on the slot job with the operator actor and reason intact")]
    public async Task Applied_command_evidence_event_is_always_emitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<OutboxOperations>();
        // SeedJobAsync writes event recording Off - exactly the case that proves the event has no recording gate.
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, ct: ct);
        await Db.From<JobStateEntity>()
            .InsertAsync<long>(
                new JobStateEntity
                {
                    Id = jobId,
                    NamespaceId = TestNamespaceId,
                    Status = JobStatusCode.Ready,
                    Priority = JobPriorityCode.Critical,
                    // Ready carries its due instant the way register_schedules writes the real
                    // slot row; ck_jobs_state_ready_due rejects the pair otherwise.
                    NextRunAtUtc = DateTime.UtcNow,
                    ExecutionNumber = 1,
                },
                ct
            );

        await store.RecordOutboxEventAsync(
            new RecordOutboxEventCommand(jobId, EventCode.OutboxDiscarded, "marko", "poison batch - 2 row(s): [a, b]"),
            ct
        );

        var events = await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct);
        var evidence = Assert.Single(events);
        Assert.Equal(EventCode.OutboxDiscarded, evidence.EventCode);
        Assert.Equal("marko", EventDetail(evidence, "actor"));
        Assert.Equal("poison batch - 2 row(s): [a, b]", evidence.EventMessage);
        Assert.Equal(1, evidence.ExecutionNumber);
    }
}
