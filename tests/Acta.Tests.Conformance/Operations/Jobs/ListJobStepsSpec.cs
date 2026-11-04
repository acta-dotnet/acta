using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for ListJobSteps: a job's step slots page newest first with their state, each slot once.</summary>
[ConformanceSpec(
    "job-payload-reads.steps",
    "ListJobSteps pages a job's step slots newest first",
    Area = "Reads",
    Contract = "ListJobSteps pages a job's step slots newest first with status and attempt, each slot once across pages, and an empty page for a job without a row."
)]
public sealed class ListJobStepsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "ListJobSteps pages three steps newest first to a null cursor")]
    public async Task Steps_page_newest_first_to_the_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var fence = await ExecutionFenceStaging.ExecutingAsync(
            Db,
            (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId,
            ct
        );
        foreach (var name in new[] { "charge", "ship", "notify" })
        {
            await execution.StartJobStepAsync(fence, name, atMostOnce: false, ct);
        }

        var seen = new List<ListJobStepsItem>();
        var query = new ListJobStepsQuery(JobLookup.ById(fence.JobId), PageSize: 2, IncludeTotal: true);
        for (var pages = 0; ; pages++)
        {
            Assert.True(pages < 4, "the cursor did not reach the end in four pages");
            var page = await Jobs.ListStepsAsync(query, ct);
            Assert.Equal(3, page.TotalCount);
            seen.AddRange(page.Items);
            if (page.NextCursor is null)
            {
                break;
            }

            query = query with { Cursor = page.NextCursor };
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Select(step => step.Name).Distinct().Count());
        Assert.All(seen, step => Assert.Equal(StepStatusCode.Pending, step.Status));
        Assert.All(seen, step => Assert.Equal(1, step.AttemptNumber));
        // Newest first: each slot was created no later than the one listed before it.
        Assert.True(seen.Zip(seen.Skip(1)).All(pair => pair.First.CreatedAtUtc >= pair.Second.CreatedAtUtc));
    }

    [Fact(DisplayName = "ListJobSteps orders steps started together by name, newest name first, across pages")]
    public async Task Steps_started_together_page_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = (
            await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(4, 5))), ct)
        ).JobId;
        // One statement: every dialect stamps the three rows with the same created_at_utc, so only the name orders them.
        await using (var connection = await Db.OpenConnectionAsync(ct))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {Db.Schema}.steps (job_id, step_name, step_status_code, attempt_number, result_format_id) VALUES ({jobId}, 'a', 10, 1, 0), ({jobId}, 'b', 10, 1, 0), ({jobId}, 'c', 10, 1, 0)";
            Assert.Equal(3, await cmd.ExecuteNonQueryAsync(ct));
        }

        var first = await Jobs.ListStepsAsync(new ListJobStepsQuery(JobLookup.ById(jobId), PageSize: 2), ct);
        var rest = await Jobs.ListStepsAsync(new ListJobStepsQuery(JobLookup.ById(jobId), PageSize: 2, Cursor: first.NextCursor), ct);

        Assert.Equal(["c", "b", "a"], first.Items.Concat(rest.Items).Select(step => step.Name));
        Assert.Null(rest.NextCursor);
    }

    [Fact(DisplayName = "ListJobSteps answers an empty page for a missing id")]
    public async Task Steps_empty_for_a_missing_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var missing = await Jobs.ListStepsAsync(new ListJobStepsQuery(JobLookup.ById(long.MaxValue), IncludeTotal: true), ct);

        Assert.Empty(missing.Items);
        Assert.False(missing.HasMore);
        Assert.Equal(0, missing.TotalCount);
    }
}
