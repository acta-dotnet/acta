using Acta.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Testing;

/// <summary>
/// The test host names the schema its runtime actually uses, so direct SQL through <see cref="IActaTestHost.Schema"/>
/// reaches its tables: on SQLite that is <c>main</c>, whatever schema name the host offered the start callback. It
/// stays readable after the host is disposed, where cleanup reads it.
/// </summary>
public sealed class ActaTestHostSchemaTests
{
    [Fact]
    public async Task A_default_sqlite_host_names_the_schema_its_tables_live_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"acta-host-schema-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
        try
        {
            var host = await ActaTestHost.StartAsync(
                (j, _) =>
                {
                    j.UseSqlite(o =>
                    {
                        o.ConnectionString = connectionString;
                        o.ApplyMigrationsOnStartup = true;
                    });
                    j.Run<TestJobs.TestJobsManifest>("host-schema", ownerTeam: "test");
                },
                new ActaTestHostOptions { ConfigureServices = s => s.Configure<JobsOptions>(o => o.RegisterSystemJobs = false) },
                ct
            );
            try
            {
                Assert.Equal("main", host.Schema);
                var job = await host.Jobs.EnqueueAsync(
                    new EnqueueJobRequest("host-schema", "add-numbers", JobPayload.Json(new TestJobs.AddNumbers(1, 2))),
                    ct
                );

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT count(*) FROM {host.Schema}.jobs WHERE job_id = {job.JobId}";
                Assert.Equal(1L, await count.ExecuteScalarAsync(ct));
            }
            finally
            {
                await host.DisposeAsync();
            }

            Assert.Equal("main", host.Schema);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }
}
