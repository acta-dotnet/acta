using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for ListJobCheckpoints: a job's checkpoints page by kind then name with state and value intact.</summary>
[ConformanceSpec(
    "job-payload-reads.checkpoints",
    "ListJobCheckpoints pages a job's checkpoints by kind then name",
    Area = "Reads",
    Contract = "ListJobCheckpoints pages a job's checkpoints by kind then name, values intact, empty for none or no job, its cursor passing a kind this build does not know."
)]
public sealed class ListJobCheckpointsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "ListJobCheckpoints lists variable and signal checkpoints with kind, state, and value round-tripped")]
    public async Task Checkpoints_round_trip_kind_state_and_value()
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
        Assert.NotNull(variable.Value);
        Assert.Equal(variableValue.Format.Id, variable.Value!.Value.Format.Id);
        Assert.Equal(variableValue.Data.ToArray(), variable.Value.Value.Data.ToArray());

        var signal = items[1];
        Assert.Equal(CheckpointKindCode.Signal, signal.Kind);
        Assert.Equal("s.ready", signal.Name);
        Assert.Equal(CheckpointStatusCode.Set, signal.Status);
        Assert.NotNull(signal.Value);
        Assert.Equal(signalValue.Data.ToArray(), signal.Value!.Value.Data.ToArray());
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
                    value.Data.ToArray(),
                    new JobControlInput("op", null, "seed")
                ),
                ct
            );
}
