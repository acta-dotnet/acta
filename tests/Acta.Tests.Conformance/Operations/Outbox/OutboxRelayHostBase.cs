using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Base for the operator-verb specs that run against a real relay host: the per-test namespace registers
/// the test manifest and an outbox relay over its own source table, so it owns a <c>sys.outbox</c> slot and
/// this host can read the source. Declares no <c>[Fact]</c>.
/// </summary>
public abstract class OutboxRelayHostBase : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private protected string SourceTable => "acta_outbox_op_" + TestId;

    private protected IOutbox Outbox => Operations.Outbox;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run(
                testNamespace,
                w =>
                {
                    w.OwnerTeam = "test";
                    w.Description = GetType().FullName;
                    w.AddManifest<TestJobs.TestJobsManifest>();
                    w.AddOutboxRelay(
                        "operator-src",
                        source =>
                        {
                            source.Table = SourceTable;
                            Fixture.ApplyOutboxSource(source);
                        }
                    );
                }
            );
        });
        services.Configure<JobsOptions>(o => o.RegisterSystemJobs = false);
    }

    protected override async ValueTask AfterInitializeAsync()
    {
        await Fixture.ApplyOutboxDdlAsync(SourceTable);
        await base.AfterInitializeAsync();
    }

    /// <summary>Stages one row the relay already quarantined after five failed deliveries.</summary>
    private protected async Task<Guid> SeedQuarantinedAsync(string dedup)
    {
        var when = DateTime.UtcNow.AddMinutes(-5);
        var row = new OutboxSeed(
            OutboxId: Guid.NewGuid(),
            JobNamespace: TestNamespace,
            JobName: "echo",
            InputFormatId: 0,
            Input: null,
            DeduplicationKey: dedup,
            PriorityCode: null,
            CreatedAtUtc: when,
            NextAttemptAtUtc: when,
            StatusCode: (byte)OutboxStatusCode.Quarantined,
            FailureCount: 5
        );
        await Fixture.SeedOutboxRowAsync(SourceTable, row);
        return row.OutboxId;
    }

    /// <summary>Runs one pass of this namespace's <c>sys.outbox</c> slot, which applies the parked operator commands first.</summary>
    private protected async Task<long> RunRelayPassAsync(CancellationToken ct)
    {
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, namespaceId, "sys.outbox", ct);
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, slotId, ct));
        return slotId;
    }
}
