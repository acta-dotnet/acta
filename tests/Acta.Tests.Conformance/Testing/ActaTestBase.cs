using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Root test base for the append-only Testing model. Each test instance gets a fresh DI
/// <see cref="IServiceProvider"/> and a unique <see cref="TestNamespace"/> name; the shared
/// <c>acta_test</c> schema is bootstrapped once per process.
/// </summary>
/// <remarks>
/// Row cleanup is intentional non-behavior: tests leave their rows behind so accumulated state is
/// inspectable after the run. Reset is an explicit operator action via
/// <c>DatabaseSetup.ResetActaTestSchema</c>.
/// </remarks>
public abstract class ActaTestBase<TFixture> : IAsyncLifetime
    where TFixture : IConformanceFixture, new()
{
    protected TFixture Fixture { get; } = new();

    protected IIntegrationSchema Schema { get; private set; } = null!;

    protected string TestNamespace { get; private set; } = null!;

    /// <summary>
    /// Per-test identity token (12 hex chars, 48 random bits). Suffixes <see cref="TestNamespace"/>
    /// and every <see cref="Unique"/>, so all rows a test leaves behind are joinable by one token.
    /// </summary>
    protected string TestId { get; private set; } = null!;

    protected IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The per-test production database session used by Acta's table reader: reads via <c>From&lt;T&gt;</c>,
    /// writes via <c>InsertAsync</c> / <c>UpdateOnlyAsync</c> / <c>DeleteAsync</c>.
    /// </summary>
    private protected IDbSession Db => Services.GetRequiredService<IDbSession>();

    public async ValueTask InitializeAsync()
    {
        Schema = await ActaSharedDatabase.EnsureReadyAsync(Fixture);

        TestId = Random.Shared.NextInt64(1L << 48).ToString("x12", CultureInfo.InvariantCulture);
        TestNamespace = ActaTestNames.CreateNamespace(GetType(), TestContext.Current.TestMethod?.MethodName, TestId);

        var services = new ServiceCollection();
        ConfigureServices(services, TestNamespace);
        Services = services.BuildServiceProvider(validateScopes: true);

        await AfterInitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await BeforeDisposeAsync();

        if (Services is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        // Intentionally no row cleanup.
    }

    /// <summary>
    /// Per-test needle: <c>{name}-{TestId}</c>. Mandatory for GLOBAL keyspaces
    /// (e.g. <c>locks.lock_key</c>, which carries no namespace); preferred over ad-hoc Guids
    /// anywhere a test needs a unique name or key (deduplication keys, extra namespaces), since the shared token
    /// makes every row a test leaves behind joinable.
    /// </summary>
    protected string Unique(string name) => name + "-" + TestId;

    /// <summary>
    /// Override to register services in the per-test DI container. Default is an empty container.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services, string testNamespace) { }

    /// <summary>Override to run extra setup after <see cref="Services"/> is built.</summary>
    protected virtual ValueTask AfterInitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Override to run extra teardown before <see cref="Services"/> is disposed.</summary>
    protected virtual ValueTask BeforeDisposeAsync() => ValueTask.CompletedTask;

    // Thin reads over Acta's From<T> table reader shared by every spec so they aren't copy-pasted. The
    // shared acta_test schema is append-only, so isolation is by id / namespace filter.

    private protected async Task<TestJobRow> ReadJobAsync(long jobId, CancellationToken ct)
    {
        var job = await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);
        var jobState = await Db.From<JobStateEntity>().Where(r => r.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(jobState);
        return new TestJobRow(job!, jobState!);
    }

    private protected async Task<EventEntity> ReadLatestEventAsync(long jobId, EventCode code, CancellationToken ct)
    {
        var rows = await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId && e.EventCode == code)
            .ToListAsync(ct);
        Assert.NotEmpty(rows);
        return rows.OrderByDescending(e => e.Id).First();
    }

    private protected async Task<EventEntity> ReadSingleEventAsync(long jobId, EventCode code, CancellationToken ct)
    {
        var rows = await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId && e.EventCode == code)
            .ToListAsync(ct);
        return Assert.Single(rows);
    }

    protected async Task<int> CountEventsAsync(long jobId, EventCode code, CancellationToken ct)
    {
        return await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId && e.EventCode == code)
            .CountAsync(ct);
    }

    /// <summary>How many of the job's attempts yielded with <paramref name="outcome"/> (rescheduled, suspended, paused, cancelled).</summary>
    protected async Task<int> CountYieldedAsync(long jobId, string outcome, CancellationToken ct) =>
        (
            await Db.From<EventEntity>()
                .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId && e.EventCode == EventCode.JobExecutionYielded)
                .ToListAsync(ct)
        ).Count(e => EventDetail(e, "outcome") == outcome);

    /// <summary>How many of the job's events record <paramref name="reason"/> in their details.</summary>
    protected async Task<int> CountReasonAsync(long jobId, string reason, CancellationToken ct) =>
        (await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct)).Count(e =>
            EventDetail(e, "reason") == reason
        );

    /// <summary>The named text fact of an event's JSON details, or null when it records none.</summary>
    private protected static string? EventDetail(EventEntity e, string name) => EventDetails.Read(e.DetailsFormatId, e.Details, name);

    protected async Task<int> CountVariableAsync(long jobId, string name, CancellationToken ct)
    {
        return await Db.From<CheckpointEntity>()
            .Where(v => v.JobId == jobId && v.Kind == CheckpointKindCode.Variable && v.CheckpointName == name)
            .CountAsync(ct);
    }

    private protected async Task<IReadOnlyList<CheckpointEntity>> ReadSignalsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<CheckpointEntity>()
            .Where(s => s.JobId == jobId && (s.Kind == CheckpointKindCode.Signal || s.Kind == CheckpointKindCode.ChildLatch))
            .ToListAsync(ct);
    }

    private protected async Task<IReadOnlyList<AlertEntity>> ReadAlertsAsync(int namespaceId, CancellationToken ct)
    {
        return await Db.From<AlertEntity>().Where(a => a.NamespaceId == namespaceId).ToListAsync(ct);
    }

    /// <summary>
    /// One past the newest event id recorded against <paramref name="jobId"/>: the id the next event
    /// written for that job would take. Specs standing in for a projected recovery pass it where the
    /// real <c>sys.alerts</c> projector passes the success event's id, so the alert store's
    /// projected-event high-water guard sees an id newer than anything the row has absorbed.
    /// </summary>
    private protected async Task<long> NextEventIdAsync(long jobId, CancellationToken ct)
    {
        var events = await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct);
        return events.Count == 0 ? 1L : events.Max(e => e.Id) + 1L;
    }
}

/// <summary>
/// Test-side composite of one job's split rows: the identity/input row and its 1:1 jobs_state row.
/// Exposes a flat property surface so specs keep asserting job.Status / job.LeasedByWorkerId
/// without caring which physical row carries it.
/// </summary>
internal sealed record TestJobRow(JobEntity Job, JobStateEntity JobState)
{
    public long Id => Job.Id;
    public Guid JobRef => Job.JobRef;
    public int NamespaceId => Job.NamespaceId;
    public int DefinitionId => Job.DefinitionId;
    public int? TenantId => Job.TenantId;
    public long? ParentJobId => Job.ParentJobId;
    public long? LineageRootJobId => Job.LineageRootJobId;
    public string? DeduplicationKey => Job.DeduplicationKey;
    public string? CorrelationKey => Job.CorrelationKey;
    public string? ConcurrencyKey => Job.ConcurrencyKey;
    public byte InputFormatId => Job.InputFormatId;
    public EventRecordingCode EventRecording => Job.EventRecording;
    public DateTime CreatedAtUtc => Job.CreatedAtUtc;
    public JobStatusCode Status => JobState.Status;
    public JobPriorityCode Priority => JobState.Priority;
    public DateTime? NextRunAtUtc => JobState.NextRunAtUtc;
    public int ExecutionNumber => JobState.ExecutionNumber;
    public int FailureCount => JobState.FailureCount;
    public DateTime? RetentionUntilUtc => JobState.RetentionUntilUtc;
    public DateTime ModifiedAtUtc => JobState.ModifiedAtUtc;
    public int Version => JobState.Version;
    public int? LeasedByWorkerId => JobState.LeasedByWorkerId;
    public DateTime? LeaseExpiresAtUtc => JobState.LeaseExpiresAtUtc;
}
