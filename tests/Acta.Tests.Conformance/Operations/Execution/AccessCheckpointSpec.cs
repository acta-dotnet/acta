using System.Data.Common;
using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>access_checkpoint</c>, the one CRUD call behind job variables and the progress slot.</summary>
[ConformanceSpec(
    "access-checkpoint.crud",
    "A checkpoint slot is set, read, kept on first write, and deleted by one call",
    Area = "Execution",
    Contract = "A slot keyed by job, kind, and name is overwritten by Set, kept by GetOrSet, removed by Delete, and never written without a payload."
)]
public sealed class AccessCheckpointSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private static readonly byte Json = JobPayloadFormat.Json.Id;

    private IExecutionOperations Execution => Services.GetRequiredService<IExecutionOperations>();

    [Fact(DisplayName = "Set inserts a slot and a later Set overwrites its value in place, one version on")]
    public async Task Set_inserts_then_overwrites_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        Assert.Equal(1, (await SlotAsync(AccessCheckpointAction.Set, job, "color", Value("red"), ct)).Found);
        var inserted = Assert.Single(await SlotsAsync(job, ct));
        Assert.Equal(CheckpointKindCode.Variable, inserted.Kind);
        Assert.Null(inserted.Status);
        Assert.Equal(Value("red"), inserted.Value);
        Assert.Equal(0, inserted.Version);

        Assert.Equal(1, (await SlotAsync(AccessCheckpointAction.Set, job, "color", Value("blue"), ct)).Found);

        var overwritten = Assert.Single(await SlotsAsync(job, ct));
        Assert.Equal(Value("blue"), overwritten.Value);
        Assert.Equal(1, overwritten.Version);
        var read = await SlotAsync(AccessCheckpointAction.Get, job, "color", null, ct);
        Assert.Equal(1, read.Found);
        Assert.Equal(Json, read.ValueFormatId);
        Assert.Equal(Value("blue"), read.Value);
        Assert.Equal(1, read.Version);
    }

    [Fact(DisplayName = "Get, Exists, and Delete answer not found for a slot that was never written")]
    public async Task Missing_slot_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        foreach (var action in new[] { AccessCheckpointAction.Get, AccessCheckpointAction.Exists, AccessCheckpointAction.Delete })
        {
            var row = await SlotAsync(action, job, "absent", null, ct);
            Assert.Equal(0, row.Found);
            Assert.Null(row.Value);
            Assert.Null(row.Version);
        }
    }

    [Fact(DisplayName = "Delete removes the slot and reports it once, and a second Delete finds nothing")]
    public async Task Delete_removes_the_slot_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        await SlotAsync(AccessCheckpointAction.Set, job, "doomed", Value("x"), ct);
        Assert.Equal(1, (await SlotAsync(AccessCheckpointAction.Exists, job, "doomed", null, ct)).Found);

        Assert.Equal(1, (await SlotAsync(AccessCheckpointAction.Delete, job, "doomed", null, ct)).Found);
        Assert.Equal(0, (await SlotAsync(AccessCheckpointAction.Delete, job, "doomed", null, ct)).Found);

        Assert.Equal(0, (await SlotAsync(AccessCheckpointAction.Exists, job, "doomed", null, ct)).Found);
        Assert.Empty(await SlotsAsync(job, ct));
    }

    [Fact(DisplayName = "GetOrSet stores the first value and hands it back unchanged to every later caller")]
    public async Task Get_or_set_keeps_the_first_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        var first = await SlotAsync(AccessCheckpointAction.GetOrSet, job, "seed", Value("first"), ct);
        var second = await SlotAsync(AccessCheckpointAction.GetOrSet, job, "seed", Value("second"), ct);

        Assert.Equal(1, first.Found);
        Assert.Equal(Value("first"), first.Value);
        Assert.Equal(0, first.Version);
        Assert.Equal(1, second.Found);
        Assert.Equal(Value("first"), second.Value);
        Assert.Equal(0, second.Version);
        var stored = Assert.Single(await SlotsAsync(job, ct));
        Assert.Equal(Value("first"), stored.Value);
        Assert.Equal(0, stored.Version);
    }

    [Fact(DisplayName = "The same name under another kind or another job is a different slot")]
    public async Task Slots_are_keyed_by_job_kind_and_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var other = await EnqueueAsync(ct);
        await SlotAsync(AccessCheckpointAction.Set, job, "shared", Value("variable"), ct);

        Assert.Equal(0, (await SlotAsync(AccessCheckpointAction.Exists, other, "shared", null, ct)).Found);
        Assert.Equal(
            0,
            (
                await Execution.AccessCheckpointAsync(
                    Command(AccessCheckpointAction.Exists, job, "shared", null, CheckpointKindCode.Progress),
                    ct
                )
            ).Found
        );

        await Execution.AccessCheckpointAsync(
            Command(AccessCheckpointAction.Set, job, "shared", Value("progress"), CheckpointKindCode.Progress),
            ct
        );

        Assert.Equal(Value("variable"), (await SlotAsync(AccessCheckpointAction.Get, job, "shared", null, ct)).Value);
        Assert.Equal(2, (await SlotsAsync(job, ct)).Count);
    }

    [Fact(DisplayName = "A write without a payload is rejected and stores nothing")]
    public async Task Write_without_a_payload_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Execution.AccessCheckpointAsync(
                new AccessCheckpointCommand(AccessCheckpointAction.Set, job, CheckpointKindCode.Variable, "empty", 0, null),
                ct
            )
        );
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Execution.AccessCheckpointAsync(
                new AccessCheckpointCommand(AccessCheckpointAction.GetOrSet, job, CheckpointKindCode.Variable, "empty", Json, null),
                ct
            )
        );

        Assert.Empty(await SlotsAsync(job, ct));
    }

    private async Task<long> EnqueueAsync(CancellationToken ct) =>
        (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId;

    private static byte[] Value(string text) => Encoding.UTF8.GetBytes($"\"{text}\"");

    private static AccessCheckpointCommand Command(
        AccessCheckpointAction action,
        long jobId,
        string name,
        byte[]? value,
        CheckpointKindCode kind = CheckpointKindCode.Variable
    ) => new(action, jobId, kind, name, value is null ? (byte)0 : Json, value);

    private Task<AccessCheckpointResponse> SlotAsync(
        AccessCheckpointAction action,
        long jobId,
        string name,
        byte[]? value,
        CancellationToken ct
    ) => Execution.AccessCheckpointAsync(Command(action, jobId, name, value), ct);

    private Task<IReadOnlyList<CheckpointEntity>> SlotsAsync(long jobId, CancellationToken ct) =>
        Db.From<CheckpointEntity>().Where(c => c.JobId == jobId).ToListAsync(ct);
}
