using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// End-to-end proof that <c>AddOutboxRelay</c> registers and dispatches <c>sys.outbox</c> through the
/// real worker runtime (with the automatic framework jobs off), and that an unavailable source fails
/// only the relay tick while every other job in the namespace keeps running.
/// </summary>
[ConformanceSpec(
    "outbox.relay-dispatch",
    "AddOutboxRelay dispatches sys.outbox and a broken source fails only it",
    Area = "Outbox",
    Contract = "A worker with AddOutboxRelay registers and dispatches sys.outbox, and an unavailable source fails only that tick."
)]
public sealed class OutboxRelayDispatchSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
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
                        "wired-src",
                        source =>
                        {
                            // A source table that is never created, so the relay tick fails on first contact.
                            source.Table = "acta_outbox_absent_" + TestId;
                            Fixture.ApplyOutboxSource(source);
                        }
                    );
                }
            );
        });
        services.Configure<JobsOptions>(o => o.RegisterSystemJobs = false);
    }

    [Fact(DisplayName = "The relay registers sys.outbox (and sys.alerts) but not the automatic-only sys.retention")]
    public async Task Relay_registers_its_manifest_subset_without_automatic_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var outbox = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == "sys.outbox")
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(outbox);
        var alerts = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == "sys.alerts")
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(alerts);
        // sys.retention is automatic-only; a relay must not pull it in.
        var retention = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == "sys.retention")
            .SingleOrDefaultAsync(ct);
        Assert.Null(retention);
    }

    [Fact(DisplayName = "A broken source fails only the sys.outbox tick while ordinary jobs still complete")]
    public async Task Broken_source_fails_only_the_outbox_tick()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // An ordinary job in the same namespace runs to completion.
        await EnqueueAndRunAsync("echo", new TestJobs.Echo("before"), ct);

        // Dispatch the due sys.outbox slot: the missing source fails only this tick.
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, ns, "sys.outbox", ct);
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        // A failed tick re-arms the firing for another attempt, like any attempt within its budget.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(TestNamespace, slotId, ct));

        // Ordinary jobs keep running after the relay tick failed.
        var after = await EnqueueAndRunAsync("echo", new TestJobs.Echo("after"), ct);
        Assert.NotEqual(0, after.JobId);
    }
}
