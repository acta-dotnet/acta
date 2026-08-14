using Acta.Relational.Entities;
using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the applied-command evidence event: written against the sys.outbox slot job with
/// the operator actor, always emitted regardless of the job's audit level, because it is the only
/// trail for actions whose subject rows live (or lived) in the producer's database.
/// </summary>
[ConformanceSpec(
    "outbox-signal.evidence",
    "An applied operator command leaves an always-emitted evidence event",
    Area = "Outbox",
    Contract = "RecordApplied appends the outbox event against the slot job with the operator actor and reason evidence, even when the job's audit level records nothing else.",
    Arrange = "A ledger job with audit level Off stands in for the sys.outbox slot.",
    Act = "A discard evidence event is recorded with an actor key and reason message.",
    Assert = "Exactly one outbox.discarded event exists on the job, stamped Operator with the actor key and reason intact."
)]
[CoversStoreMethod(typeof(IOutboxSignalStore), nameof(IOutboxSignalStore.RecordAppliedAsync))]
public abstract class OutboxSignalEvidenceSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "The evidence event lands on the slot job with the operator actor and reason intact")]
    public async Task Applied_command_evidence_event_is_always_emitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IOutboxSignalStore>();
        // SeedJobAsync writes AuditLevel Off - exactly the case that proves the event has no audit gate.
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, ct: ct);
        await Db.From<JobRuntime>()
            .InsertAsync<long>(
                new JobRuntime
                {
                    Id = jobId,
                    NamespaceId = TestNamespaceId,
                    Status = JobStatusCode.Ready,
                    Priority = JobPriorityCode.Critical,
                    ExecutionNumber = 1,
                },
                ct
            );

        await store.RecordAppliedAsync(
            new RecordOutboxEventCommand(jobId, EventCode.OutboxDiscarded, "marko", "poison batch - 2 row(s): [a, b]"),
            ct
        );

        var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId).ToListAsync(ct);
        var evidence = Assert.Single(events);
        Assert.Equal(EventCode.OutboxDiscarded, evidence.EventCode);
        Assert.Equal(ActorCode.Operator, evidence.ActorCode);
        Assert.Equal("marko", evidence.ActorKey);
        Assert.Equal("poison batch - 2 row(s): [a, b]", evidence.ReasonMessage);
        Assert.Equal(1, evidence.ExecutionNumber);
    }
}
