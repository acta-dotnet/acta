using Acta.Runtime.Modules.Execution;
using Acta.Sqlite.Configuration;
using Acta.Sqlite.Services;
using Acta.Tests.Conformance.Sqlite.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Runtime;

/// <summary>
/// What ExecutionProfile.Bulk actually does on SQLite, which has no batched-completion routine. The
/// shared Bulk specs either skip here or only prove the backlog drains, so the degradation itself was
/// unpinned: Bulk must behave as Direct, meaning no completion is ever buffered for a group commit
/// (each attempt finalizes inside its own tick) and the connection takes Direct's relaxed
/// synchronous = NORMAL rather than Buffered's FULL. Provider-local because the degradation is a
/// SQLite property, not a cross-provider contract.
/// </summary>
public sealed class SqliteBulkProfileDegradationSpec : ActaRuntimeTestBase<SqliteConformanceFixture, TestJobsManifest>
{
    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.ExecutionProfile = ExecutionProfile.Bulk);
    }

    [Fact(DisplayName = "Bulk has no batched-completion path on SQLite: the routine flag is off and the batch call is refused")]
    public async Task Bulk_has_no_batched_completion_path()
    {
        var ct = TestContext.Current.CancellationToken;

        // SupportsRoutines is the flag WorkerRuntime reads to decide whether to build a CompletionSink
        // at all, so a false here is what makes Bulk degrade to Direct before any completion buffers.
        Assert.False(Services.GetRequiredService<SqliteDialect>().SupportsRoutines);

        // And the store refuses the batch outright, so the degradation cannot be bypassed by calling
        // the sink's path directly.
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Services.GetRequiredService<IExecutionStore>().CompleteExecutionsBatchAsync([], ct)
        );
    }

    [Fact(DisplayName = "A Bulk-profile job is already terminal when its tick returns, because nothing buffered it")]
    public async Task Bulk_finalizes_each_job_inside_its_own_tick()
    {
        var ct = TestContext.Current.CancellationToken;

        // No flusher runs inside RunOnceAsync. Under a real group commit the row would still be
        // Executing here and only settle on a later flush; under the degradation it is already done.
        var enqueued = await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct);

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "Bulk takes Direct's relaxed commit fsync: an owned connection reports synchronous = NORMAL")]
    public async Task Bulk_relaxes_commit_fsync_like_direct()
    {
        var ct = TestContext.Current.CancellationToken;

        // The DI-registered dialect, built from this runtime's configured Bulk profile, so the pragma
        // read back is the one every Acta-owned connection in this host opens with.
        var dialect = Services.GetRequiredService<SqliteDialect>();
        var connectionString = Services.GetRequiredService<IOptions<SqliteProviderOptions>>().Value.ConnectionString;

        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";

        // 1 = NORMAL, the value Direct selects; Buffered would report 2 = FULL.
        Assert.Equal(1L, Assert.IsType<long>(await command.ExecuteScalarAsync(ct)));
    }
}
