using System.Globalization;
using Xunit;

namespace Acta.Tests.Context;

/// <summary>A map's replay over a context that reads its children: it starts and builds only the children it has not started.</summary>
public sealed class ChildGroupReplayTests
{
    [Fact]
    public async Task A_replayed_map_starts_and_builds_only_the_children_it_has_not_started()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ReadingContext();
        ctx.Existing["grp/1"] = new EnqueueJobResponse(101, JobRef.New(), EnqueueJobAction.Deduplicated);
        ctx.Existing["grp/3"] = new EnqueueJobResponse(103, JobRef.New(), EnqueueJobAction.Deduplicated);
        var built = new List<int>();

        var outcome = await ctx.MapAsync(
            "grp",
            (int[])[1, 2, 3],
            i => i,
            i =>
            {
                built.Add(i);
                return new { };
            },
            ct
        );

        Assert.Equal([2], built);
        Assert.Equal(["children:grp/1,grp/2,grp/3", "start:grp/2", "wait:101", "wait:grp/2", "wait:103"], ctx.Events);
        Assert.Equal([101, 1, 103], outcome.Items.Select(item => item.Outcome.JobId));
        Assert.True(outcome.Succeeded);
    }

    /// <summary>
    /// A recording context with the map's read: the seeded children answer by name, and a wait on one answers that it
    /// landed Succeeded.
    /// </summary>
    private sealed class ReadingContext : RecordingJobContext
    {
        public Dictionary<string, EnqueueJobResponse> Existing { get; } = new(StringComparer.Ordinal);

        private protected override Task<IReadOnlyDictionary<string, EnqueueJobResponse>?> ListChildJobsByNameCoreAsync(
            IReadOnlyList<string> names,
            CancellationToken ct
        )
        {
            Events.Add($"children:{string.Join(',', names)}");
            return Task.FromResult<IReadOnlyDictionary<string, EnqueueJobResponse>?>(
                names.Where(Existing.ContainsKey).ToDictionary(name => name, name => Existing[name], StringComparer.Ordinal)
            );
        }

        private protected override Task<SignalCheckpointState> WaitSignalCoreAsync(string name, CancellationToken ct)
        {
            var id = long.Parse(name["sys.child.".Length..], CultureInfo.InvariantCulture);
            if (!Existing.Values.Any(child => child.JobId == id))
            {
                return base.WaitSignalCoreAsync(name, ct);
            }

            Events.Add($"wait:{id}");
            return Task.FromResult(new SignalCheckpointState(0, ChildOutcomeEnvelope.Write(id, JobStatusCode.Succeeded, 1)));
        }
    }
}
