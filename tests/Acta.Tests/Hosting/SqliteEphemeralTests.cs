using Acta.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Hosting;

/// <summary>
/// <c>UseSqliteEphemeral</c> gives a host a database in the process's memory that it alone holds: it outlives the
/// driver's pool, is shared with no other host, and is gone when its host is disposed.
/// </summary>
public sealed class SqliteEphemeralTests
{
    private const string Namespace = "ephemeral";

    [Fact]
    public async Task The_database_outlives_a_pool_clear_and_ends_with_its_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = new SqliteDialect();
        string connectionString;
        SemaphoreSlim writes;
        await using (var host = await StartAsync(ct))
        {
            connectionString = host.Services.GetRequiredService<SqlProviderOptions>().ConnectionString;
            writes = dialect.OwnedWriteQueue(connectionString);
            var job = await EnqueueAsync(host, ct);

            // With the pool's connections closed, only the one the host holds keeps the schema and the job.
            using var pooled = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(pooled);

            Assert.Equal(ActaRunStatus.Completed, await host.RunOnceAsync(job, ct));
            Assert.Equal(3, (await host.Jobs.GetResultAsync<TestJobs.AddNumbersResult>(job, ct)).Sum);
        }

        // Unpooled, so the empty database this connection opens under the old name ends with it.
        await using var after = new SqliteConnection(
            new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString
        );
        await after.OpenAsync(ct);
        await using var tables = after.CreateCommand();
        tables.CommandText = "SELECT count(*) FROM sqlite_schema";
        Assert.Equal(0L, await tables.ExecuteScalarAsync(ct));

        // Asking for the queue makes one where none is kept, so another queue than the host's says the host's went.
        Assert.NotSame(writes, dialect.OwnedWriteQueue(connectionString));
        SqliteDialect.RemoveWriteQueue(connectionString);
    }

    [Fact]
    public async Task Two_hosts_in_one_process_share_no_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = await StartAsync(ct);
        await using var second = await StartAsync(ct);

        var job = await EnqueueAsync(first, ct);

        Assert.NotNull(await first.Jobs.GetAsync(job.JobRef, ct));
        Assert.Null(await second.Jobs.GetAsync(job.JobRef, ct));
    }

    [Fact]
    public void A_second_provider_is_refused_before_the_database_is_registered()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.UseActa(builder =>
            {
                builder.UsePostgres(static _ => { });
                builder.UseSqliteEphemeral();
            })
        );

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SqliteEphemeralDatabase));
    }

    private static Task<IActaTestHost> StartAsync(CancellationToken ct) =>
        ActaTestHost.StartAsync(
            (j, _) =>
            {
                j.UseSqliteEphemeral();
                j.Run<TestJobs.TestJobsManifest>(Namespace, ownerTeam: "test");
            },
            new ActaTestHostOptions { ConfigureServices = s => s.Configure<JobsOptions>(o => o.RunSystemJobs = false) },
            ct
        );

    private static ValueTask<EnqueueJobResponse> EnqueueAsync(IActaTestHost host, CancellationToken ct) =>
        host.Jobs.EnqueueAsync(new EnqueueJobRequest(Namespace, "add-numbers", JobPayload.Json(new TestJobs.AddNumbers(1, 2))), ct);
}
