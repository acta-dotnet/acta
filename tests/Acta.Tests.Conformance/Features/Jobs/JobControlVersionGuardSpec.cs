using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// The optional expectedVersion token on the job control verbs: null writes unconditionally, a matching
/// token applies and bumps the version, a stale token is VersionConflict and writes nothing.
/// </summary>
[ConformanceSpec(
    "job.control-version-guard",
    "A job control verb takes an optional expected version and refuses a stale one.",
    Area = "Control",
    Contract = "A null expectedVersion applies unconditionally, a matching one applies and returns version + 1, and a stale one is VersionConflict that writes nothing.",
    Arrange = "A Ready job whose runtime version is read before each attempt.",
    Act = "PauseAsync and RescheduleAsync are invoked with a stale token, a matching token, and no token.",
    Assert = "The stale attempt leaves status, version and events untouched, the matching and null attempts apply and bump the version, and an unknown lookup is NotFound."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PauseJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RescheduleJobAsync))]
public abstract class JobControlVersionGuardSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "PauseAsync with a stale expected version is VersionConflict and writes nothing")]
    public async Task Stale_token_conflicts_without_writing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var before = await ReadJobAsync(job, ct);

        var result = await Jobs.PauseAsync(JobLookup.ById(job), "ops", "spec-actor", before.Version + 5, ct);

        Assert.Equal(ControlAction.VersionConflict, result.Action);
        Assert.Equal(before.Status, result.Status);
        Assert.Equal(before.Version, result.Version);

        var after = await ReadJobAsync(job, ct);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(0, await CountEventsAsync(job, EventCode.JobPaused, ct));
    }

    [Fact(DisplayName = "PauseAsync with the current expected version applies and returns the bumped version")]
    public async Task Matching_token_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var before = await ReadJobAsync(job, ct);

        var result = await Jobs.PauseAsync(JobLookup.ById(job), "ops", "spec-actor", before.Version, ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Paused, result.Status);
        Assert.Equal(before.Version + 1, result.Version);

        var after = await ReadJobAsync(job, ct);
        Assert.Equal(JobStatusCode.Paused, after.Status);
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact(DisplayName = "A null expected version applies whatever the row's version is")]
    public async Task Null_token_applies_unconditionally()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        // Two writes with no token in a row: the second still applies against the version the first left.
        var first = await Jobs.PauseAsync(JobLookup.ById(job), ct: ct);
        var second = await Jobs.RescheduleAsync(JobLookup.ById(job), DateTime.UtcNow.AddMinutes(5), ct: ct);

        Assert.Equal(ControlAction.Applied, first.Action);
        Assert.Equal(ControlAction.Applied, second.Action);
        Assert.Equal(first.Version + 1, second.Version);
    }

    [Fact(DisplayName = "RescheduleAsync with a stale expected version is VersionConflict and leaves the cursor alone")]
    public async Task Reschedule_stale_token_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var before = await ReadJobAsync(job, ct);

        var result = await Jobs.RescheduleAsync(
            JobLookup.ById(job),
            DateTime.UtcNow.AddMinutes(5),
            "ops",
            "spec-actor",
            before.Version + 5,
            ct
        );

        Assert.Equal(ControlAction.VersionConflict, result.Action);
        Assert.Equal(before.Version, result.Version);

        var after = await ReadJobAsync(job, ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.NextRunAtUtc, after.NextRunAtUtc);
        Assert.Equal(0, await CountEventsAsync(job, EventCode.JobRescheduled, ct));
    }

    [Fact(DisplayName = "RescheduleAsync with the current expected version applies and bumps the version")]
    public async Task Reschedule_matching_token_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var before = await ReadJobAsync(job, ct);
        var near = DateTime.UtcNow.AddMinutes(5);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(job), near, "ops", "spec-actor", before.Version, ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(before.Version + 1, result.Version);

        var after = await ReadJobAsync(job, ct);
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact(DisplayName = "An unknown lookup is NotFound with a null version whether or not a token is supplied")]
    public async Task Unknown_lookup_is_notfound_with_null_version()
    {
        var ct = TestContext.Current.CancellationToken;

        var guarded = await Jobs.PauseAsync(JobLookup.ById(999_999_999_999L), null, null, 1, ct);
        var unguarded = await Jobs.PauseAsync(JobLookup.ById(999_999_999_999L), ct: ct);

        Assert.Equal(ControlAction.NotFound, guarded.Action);
        Assert.Null(guarded.Version);
        Assert.Equal(ControlAction.NotFound, unguarded.Action);
        Assert.Null(unguarded.Version);
    }

    [Fact(DisplayName = "GetAsync exposes the runtime version a control verb takes back as its expected version")]
    public async Task Detail_exposes_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var row = await ReadJobAsync(job, ct);

        var detail = await Jobs.GetAsync(JobLookup.ById(job), ct);

        Assert.NotNull(detail);
        Assert.Equal(row.Version, detail!.Version);

        var result = await Jobs.PauseAsync(JobLookup.ById(job), null, null, detail.Version, ct);
        Assert.Equal(ControlAction.Applied, result.Action);
    }

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(2, 3)),
                NextRunAtUtc: DateTime.UtcNow.AddDays(30)
            ),
            ct
        );
        return enqueued.JobId;
    }
}
