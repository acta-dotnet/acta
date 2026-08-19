using System.Globalization;
using Acta.Relational.Entities;
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
    /// and every <see cref="TestKey"/>, so all rows a test leaves behind are joinable by one token.
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
    /// anywhere a test needs a unique name (deduplication keys, extra namespaces), since the shared token
    /// makes every row a test leaves behind joinable.
    /// </summary>
    protected string TestKey(string name) => name + "-" + TestId;

    /// <summary>
    /// Override to register services in the per-test DI container. Default is an empty container.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services, string testNamespace) { }

    /// <summary>Override to run extra setup after <see cref="Services"/> is built.</summary>
    protected virtual ValueTask AfterInitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Override to run extra teardown before <see cref="Services"/> is disposed.</summary>
    protected virtual ValueTask BeforeDisposeAsync() => ValueTask.CompletedTask;

    // ---------- shared entity reads ----------
    // Thin reads over Acta's From<T> table reader shared by every spec so they aren't copy-pasted. The
    // shared acta_test schema is append-only, so isolation is by id / namespace filter.

    private protected async Task<TestJobRow> ReadJobAsync(long jobId, CancellationToken ct)
    {
        var job = await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);
        var runtime = await Db.From<JobRuntime>().Where(r => r.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(runtime);
        return new TestJobRow(job!, runtime!);
    }

    private protected async Task<JobEvent> ReadLatestEventAsync(long jobId, EventCode code, CancellationToken ct)
    {
        var rows = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == code).ToListAsync(ct);
        Assert.NotEmpty(rows);
        return rows.OrderByDescending(e => e.Id).First();
    }

    private protected async Task<JobEvent> ReadSingleEventAsync(long jobId, EventCode code, CancellationToken ct)
    {
        var rows = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == code).ToListAsync(ct);
        return Assert.Single(rows);
    }

    protected async Task<int> CountEventsAsync(long jobId, EventCode code, CancellationToken ct)
    {
        return await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == code).CountAsync(ct);
    }

    protected async Task<int> CountFinishedWithStatusAsync(long jobId, ExecutionStatusCode status, CancellationToken ct)
    {
        return await Db.From<JobEvent>()
            .Where(e => e.JobId == jobId && e.EventCode == EventCode.JobExecutionFinished && e.ExecutionStatus == status)
            .CountAsync(ct);
    }

    protected async Task<int> CountVariableAsync(long jobId, string name, CancellationToken ct)
    {
        return await Db.From<JobCheckpoint>()
            .Where(v => v.JobId == jobId && v.Kind == JobCheckpointKindCode.Variable && v.Name == name)
            .CountAsync(ct);
    }

    private protected async Task<IReadOnlyList<JobCheckpoint>> ReadSignalsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<JobCheckpoint>()
            .Where(s => s.JobId == jobId && (s.Kind == JobCheckpointKindCode.Signal || s.Kind == JobCheckpointKindCode.ChildLatch))
            .ToListAsync(ct);
    }

    private protected async Task<IReadOnlyList<JobAlert>> ReadAlertsAsync(short namespaceId, CancellationToken ct)
    {
        return await Db.From<JobAlert>().Where(a => a.NamespaceId == namespaceId).ToListAsync(ct);
    }

    /// <summary>
    /// One past the newest event id recorded against <paramref name="jobId"/>: the id the next event
    /// written for that job would take. Specs standing in for a projected recovery pass it where the
    /// real <c>sys.alerts</c> projector passes the success event's id, so the alert store's
    /// projected-event high-water guard sees an id newer than anything the row has absorbed.
    /// </summary>
    private protected async Task<long> NextEventIdAsync(long jobId, CancellationToken ct)
    {
        var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId).ToListAsync(ct);
        return events.Count == 0 ? 1L : events.Max(e => e.Id) + 1L;
    }
}

/// <summary>
/// Test-side composite of one job's split rows: the identity/input row and its 1:1 runtime row.
/// Exposes a flat property surface so specs keep asserting job.Status / job.LeasedByWorkerId
/// without caring which physical row carries it.
/// </summary>
internal sealed record TestJobRow(Job Job, JobRuntime Runtime)
{
    public long Id => Job.Id;
    public Guid JobRef => Job.JobRef;
    public short NamespaceId => Job.NamespaceId;
    public int DefinitionId => Job.DefinitionId;
    public int? TenantId => Job.TenantId;
    public long? ParentId => Job.ParentId;
    public long? LineageRootId => Job.LineageRootId;
    public string? DeduplicationKey => Job.DeduplicationKey;
    public string? CorrelationKey => Job.CorrelationKey;
    public string? ExclusiveKey => Job.ExclusiveKey;
    public byte InputFormatId => Job.InputFormatId;
    public JobAuditLevelCode AuditLevel => Job.AuditLevel;
    public DateTime CreatedAtUtc => Job.CreatedAtUtc;
    public JobStatusCode Status => Runtime.Status;
    public JobPriorityCode Priority => Runtime.Priority;
    public DateTime? NextRunAtUtc => Runtime.NextRunAtUtc;
    public int ExecutionNumber => Runtime.ExecutionNumber;
    public short FailureCount => Runtime.FailureCount;
    public DateTime? RetentionUntilUtc => Runtime.RetentionUntilUtc;
    public DateTime ModifiedAtUtc => Runtime.ModifiedAtUtc;
    public int Version => Runtime.Version;
    public int? LeasedByWorkerId => Runtime.LeasedByWorkerId;
    public DateTime? LeaseExpiresAtUtc => Runtime.LeaseExpiresAtUtc;
}
