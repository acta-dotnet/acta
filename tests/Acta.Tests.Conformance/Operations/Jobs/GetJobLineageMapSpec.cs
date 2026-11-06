using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobLineageMap</c>: the five-result-set lineage read that returns the focus job,
/// its ancestors (root first), its steps and checkpoints, and its direct children, each capped set at its
/// fetch limit, and <c>null</c> for an unknown id. Exercises the recursive ancestor walk, the caps, and
/// the absent-row sentinel across the shared (pg/sqlite) and SQL Server SQL variants.
/// </summary>
[ConformanceSpec(
    "get-job-lineage-map.point-read",
    "GetJobLineageMap returns the focus job with ancestors and children or null",
    Area = "Reads",
    Contract = "GetJobLineageMap returns the focus job, its nearest ancestors root-first, newest steps, checkpoints, and capped direct children, or null when no row matches."
)]
public sealed class GetJobLineageMapSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private async Task<long> EnqueueAsync(long? parentId, CancellationToken ct)
    {
        var outcome = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1)), ParentJobId: parentId),
            ct
        );
        return outcome.JobId;
    }

    [Fact(DisplayName = "A known job returns its focus row, its root parent as an ancestor, and its two direct children")]
    public async Task Returns_focus_ancestors_and_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(null, ct);
        var mid = await EnqueueAsync(root, ct);
        var childA = await EnqueueAsync(mid, ct);
        var childB = await EnqueueAsync(mid, ct);

        var data = await Services.GetRequiredService<GetJobLineageMapHandler>().ReadAsync(mid, 100, ct);

        Assert.NotNull(data);
        Assert.Equal(mid, data!.Focus.JobId);
        Assert.Equal(root, data.Focus.ParentJobId);
        Assert.Equal(root, data.Focus.LineageRootJobId);
        Assert.Single(data.Ancestors);
        Assert.Equal(root, data.Ancestors[0].JobId);
        Assert.Null(data.Ancestors[0].ParentJobId);
        Assert.Equal([childA, childB], [.. data.Children.Select(c => c.JobId).OrderBy(id => id)]);
        Assert.Empty(data.Steps);
        Assert.Empty(data.Checkpoints);
    }

    [Fact(DisplayName = "Ancestors are ordered from the lineage root down to the immediate parent")]
    public async Task Ancestors_are_ordered_root_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(null, ct);
        var mid = await EnqueueAsync(root, ct);
        var leaf = await EnqueueAsync(mid, ct);

        var data = await Services.GetRequiredService<GetJobLineageMapHandler>().ReadAsync(leaf, 100, ct);

        Assert.NotNull(data);
        Assert.Equal([root, mid], [.. data!.Ancestors.Select(a => a.JobId)]);
        Assert.Empty(data.Children);
    }

    [Fact(DisplayName = "The direct-children set is capped at the fetch limit")]
    public async Task Children_are_capped_at_the_fetch_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(null, ct);
        await EnqueueAsync(root, ct);
        await EnqueueAsync(root, ct);
        await EnqueueAsync(root, ct);

        var data = await Services.GetRequiredService<GetJobLineageMapHandler>().ReadAsync(root, 2, ct);

        Assert.NotNull(data);
        Assert.Equal(2, data!.Children.Count);
    }

    [Fact(DisplayName = "Past the ancestor and step limits the read keeps the nearest ancestors and the newest steps")]
    public async Task Ancestors_and_steps_past_their_limits_keep_the_nearest()
    {
        var ct = TestContext.Current.CancellationToken;
        // A chain one ancestor past what the read walks, root first: chain[^1] is the focus, chain[0] the root it never reaches.
        var requests = Enumerable
            .Range(0, GetJobLineageMapHandler.AncestorLimit + 3)
            .Select(i => new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(i, 1))))
            .ToList();
        var chain = (await Jobs.EnqueueJobsAsync(requests, ct)).Jobs.Select(job => job.JobId).ToList();
        var focus = chain[^1];
        var parents = string.Join(" ", chain.Skip(1).Zip(chain, (child, parent) => $"WHEN {child} THEN {parent}"));
        // One statement stamps every step with the same created_at_utc, so the name orders them, the highest the newest.
        var steps = Enumerable.Range(0, GetJobLineageMapHandler.StepLimit + 2).Select(i => $"({focus}, 's{i:D3}', 10, 1, 0)");
        await using (var connection = await Db.OpenConnectionAsync(ct))
        {
            await using var link = connection.CreateCommand();
            link.CommandText =
                $"UPDATE {Db.Schema}.jobs SET parent_job_id = CASE job_id {parents} END WHERE job_id IN ({string.Join(", ", chain.Skip(1))})";
            Assert.Equal(chain.Count - 1, await link.ExecuteNonQueryAsync(ct));
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                $"INSERT INTO {Db.Schema}.steps (job_id, step_name, step_status_code, attempt_number, result_format_id) VALUES {string.Join(", ", steps)}";
            await insert.ExecuteNonQueryAsync(ct);
        }

        var data = await Services.GetRequiredService<GetJobLineageMapHandler>().ReadAsync(focus, 100, ct);
        var map = await Jobs.GetLineageMapAsync(JobLookup.ById(focus), ct: ct);

        Assert.NotNull(data);
        Assert.Equal(GetJobLineageMapHandler.AncestorLimit + 1, data!.Ancestors.Count);
        Assert.Equal(chain[1], data.Ancestors[0].JobId);
        Assert.Equal(chain[^2], data.Ancestors[^1].JobId);
        Assert.Equal(GetJobLineageMapHandler.StepLimit + 1, data.Steps.Count);
        Assert.Equal($"s{GetJobLineageMapHandler.StepLimit + 1:D3}", data.Steps[0].StepName);
        Assert.NotNull(map);
        Assert.True(map!.AncestorsHasMore);
        Assert.Equal(chain[2], map.Ancestors[0].JobId);
        Assert.True(map.StepsHasMore);
        Assert.Equal(GetJobLineageMapHandler.StepLimit, map.Steps.Count);
        Assert.Equal("s002", map.Steps[0].Name);
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = await Services.GetRequiredService<GetJobLineageMapHandler>().ReadAsync(long.MaxValue, 100, ct);

        Assert.Null(data);
    }
}
