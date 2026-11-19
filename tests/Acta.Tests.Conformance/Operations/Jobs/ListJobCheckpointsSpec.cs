using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for ListJobCheckpoints: a job's checkpoints page by kind then name with their state, each value stated by
/// its format and size and read on its own.
/// </summary>
[ConformanceSpec(
    "job-payload-reads.checkpoints",
    "ListJobCheckpoints pages a job's checkpoints by kind then name",
    Area = "Reads",
    Contract = "ListJobCheckpoints pages a job's checkpoints by kind then name, each value by format and size, empty for none or no job, its cursor passing an unknown kind."
)]
[Deploys("add-numbers")]
public sealed class ListJobCheckpointsSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ListJobCheckpointsSpec>>
{
    [Fact(DisplayName = "ListJobCheckpoints lists variable and signal checkpoints with kind, state, and each value's format and size")]
    public async Task Checkpoints_round_trip_kind_state_and_value_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var execution = Services.GetRequiredService<ExecutionOperations>();

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))),
            ct
        );

        // The variable is written as a handler writes it, under the fence of an attempt the job stands executing.
        var variableValue = JobPayload.Json("stage-two");
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);
        await CheckpointAccess.SetAsync(execution, fence, CheckpointKindCode.Variable, "fetch.status", variableValue, ct);

        var signalValue = JobPayload.Json(new AddNumbers(2, 3));
        await RaiseAsync(enqueued.JobId, "s.ready", signalValue, ct);

        var response = await Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(JobLookup.ById(enqueued.JobId)), ct);
        var items = response.Items;
        Assert.Equal(2, items.Count);
        Assert.False(response.HasMore);
        Assert.Null(response.NextCursor);

        // Ordered by kind (Variable=10 before Signal=20) then name.
        var variable = items[0];
        Assert.Equal(CheckpointKindCode.Variable, variable.Kind);
        Assert.Equal("fetch.status", variable.Name);
        Assert.Null(variable.Status);
        Assert.Equal(variableValue.Format.Id, variable.ValueFormatId);
        Assert.Equal((long)variableValue.Data.Length, variable.ValueByteLength);

        var signal = items[1];
        Assert.Equal(CheckpointKindCode.Signal, signal.Kind);
        Assert.Equal("s.ready", signal.Name);
        Assert.Equal(CheckpointStatusCode.Set, signal.Status);
        Assert.Equal(signalValue.Format.Id, signal.ValueFormatId);
        Assert.Equal((long)signalValue.Data.Length, signal.ValueByteLength);
    }

    [Fact(DisplayName = "A value's size counts stored bytes: a format with no bytes is 0, and multi-byte UTF-8 counts every byte")]
    public async Task Value_sizes_count_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 4))),
            ct
        );
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);
        const string text = "\"Zoë ✓ 日本語 😀\"";
        var wide = JobPayload.FromBytes(JobPayloadFormat.Json, System.Text.Encoding.UTF8.GetBytes(text));
        Assert.NotEqual(text.Length, wide.Data.Length);
        await CheckpointAccess.SetAsync(execution, fence, CheckpointKindCode.Variable, "empty", JobPayload.Bytes([]), ct);
        await CheckpointAccess.SetAsync(execution, fence, CheckpointKindCode.Variable, "wide", wide, ct);
        await RaiseAsync(enqueued.JobId, "s.wide", wide, ct);

        var items = (await Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(JobLookup.ById(enqueued.JobId)), ct)).Items;
        var empty = Assert.Single(items, item => item.Name == "empty");
        Assert.Equal(JobPayloadFormat.Bytes.Id, empty.ValueFormatId);
        Assert.Equal(0L, empty.ValueByteLength);
        Assert.Equal((long)wide.Data.Length, Assert.Single(items, item => item.Name == "wide").ValueByteLength);
        Assert.Equal((long)wide.Data.Length, Assert.Single(items, item => item.Name == "s.wide").ValueByteLength);
    }

    [Fact(DisplayName = "A checkpoint that holds no value is listed with format 0 and no size")]
    public async Task A_checkpoint_without_a_value_lists_no_format_and_no_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 3))),
            ct
        );
        await RaiseAsync(enqueued.JobId, "s.present", JobPayload.None, ct);

        var signal = Assert.Single(
            (await Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(JobLookup.ById(enqueued.JobId)), ct)).Items
        );
        Assert.Equal("s.present", signal.Name);
        Assert.Equal(0, signal.ValueFormatId);
        Assert.Null(signal.ValueByteLength);
    }

    [Fact(DisplayName = "ListJobCheckpoints answers an empty page for a job with no checkpoints and for a missing id")]
    public async Task Checkpoints_empty_for_no_checkpoints_and_for_a_missing_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );

        var none = await Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(JobLookup.ById(enqueued.JobId), IncludeTotal: true), ct);
        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);

        var missing = await Jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(JobLookup.ById(long.MaxValue), IncludeTotal: true), ct);
        Assert.Empty(missing.Items);
        Assert.False(missing.HasMore);
        Assert.Equal(0, missing.TotalCount);
    }

    [Fact(DisplayName = "ListJobCheckpoints pages to the end across a kind this build does not know, each checkpoint once")]
    public async Task Checkpoints_page_past_an_unknown_kind_without_repeating_rows()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))),
            ct
        );
        foreach (var name in new[] { "s.a", "s.b", "s.c" })
        {
            await RaiseAsync(enqueued.JobId, name, JobPayload.Json(name), ct);
        }

        // Two checkpoints a newer build wrote under kind 70: they decode to id 0 here, and the cursor must still step past them.
        await using (var connection = await Db.OpenConnectionAsync(ct))
        {
            foreach (var name in new[] { "future.a", "future.b" })
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"INSERT INTO {Db.Schema}.checkpoints (job_id, checkpoint_kind_code, checkpoint_name, value_format_id) VALUES ({enqueued.JobId}, 70, '{name}', 0)";
                Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
            }
        }

        var seen = new List<ListJobCheckpointsItem>();
        var query = new ListJobCheckpointsQuery(JobLookup.ById(enqueued.JobId), PageSize: 2, IncludeTotal: true);
        for (var pages = 0; ; pages++)
        {
            Assert.True(pages < 5, "the cursor did not reach the end in five pages");
            var page = await Jobs.ListCheckpointsAsync(query, ct);
            Assert.Equal(5, page.TotalCount);
            seen.AddRange(page.Items);
            if (page.NextCursor is null)
            {
                Assert.False(page.HasMore);
                break;
            }

            query = query with { Cursor = page.NextCursor };
        }

        Assert.Equal(["s.a", "s.b", "s.c", "future.a", "future.b"], seen.Select(item => item.Name));
        Assert.Equal(CheckpointKindCode.Unspecified, seen[^1].Kind);
    }

    private async Task RaiseAsync(long jobId, string name, JobPayload value, CancellationToken ct) =>
        await Services
            .GetRequiredService<SignalsOperations>()
            .RaiseJobSignalAsync(
                new RaiseJobSignalCheckpoint(
                    jobId,
                    CheckpointKindCode.Signal,
                    name,
                    value.Format.Id,
                    value.IsNone ? null : value.Data.ToArray(),
                    new JobControlInput("op", null, "seed")
                ),
                ct
            );
}
