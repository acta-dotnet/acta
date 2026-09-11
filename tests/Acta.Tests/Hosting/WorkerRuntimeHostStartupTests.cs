using System.Globalization;
using Acta.Runtime.Hosting;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acta.Tests.Hosting;

/// <summary>
/// The production hosted service's own lifecycle, against a throwaway SQLite file: what
/// <c>WorkerRuntimeHost</c> promises callers is an ordering (every provider bootstrap finishes before
/// any worker touches the catalog) and a pair of durable marks (a worker row that exists Active after
/// start and lands Stopped after a clean stop). An extra bootstrap registered beside the provider's
/// reads the catalog as it runs, so the ordering is read from the database rather than from a spy's
/// call sequence.
/// </summary>
public sealed class WorkerRuntimeHostStartupTests
{
    private const string WorkerNamespace = "host-startup";

    [Fact]
    public async Task Start_initializes_the_catalog_after_every_bootstrap_and_stop_marks_the_worker_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"acta-host-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;

        try
        {
            CatalogReadingBootstrap? extra = null;
            var services = new ServiceCollection();
            services.UseActa(j =>
            {
                j.UseSqlite(o =>
                {
                    o.ConnectionString = connectionString;
                    o.ApplyMigrationsOnStartup = true;
                });
                j.Run<TestJobs.TestJobsManifest>(WorkerNamespace, ownerTeam: "test");
                // The test process's own command line must not be mistaken for an Acta CLI invocation,
                // which would swap the hosted service under test out of the graph.
                j.DisableCli();
                j.Services.AddSingleton<IProviderBootstrap>(_ => extra = new CatalogReadingBootstrap(connectionString));
            });
            // The framework's recurring slots would be claimable while the loop runs; this test is about
            // the host's edges, not about what it executes.
            services.Configure<JobsOptions>(o => o.RegisterSystemJobs = false);

            await using var provider = services.BuildServiceProvider(validateScopes: true);
            var host = Assert.IsType<WorkerRuntimeHost>(Assert.Single(provider.GetServices<IHostedService>()));

            await host.StartAsync(ct);

            Assert.NotNull(extra);
            Assert.Equal(0, extra!.WorkersWhenItRan);
            Assert.True(await CountAsync(connectionString, "definitions", ct) > 0, "startup registered no definitions.");
            Assert.Equal(WorkerStatusCode.Active, await WorkerStatusAsync(connectionString, ct));

            await host.StopAsync(ct);

            Assert.Equal(WorkerStatusCode.Stopped, await WorkerStatusAsync(connectionString, ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Counts the worker rows it can see when the host runs it. Registered after the provider's own
    /// bootstrap, so the schema exists by then and a non-zero count would mean a worker initialized
    /// before the bootstrap chain finished.
    /// </summary>
    private sealed class CatalogReadingBootstrap(string connectionString) : IProviderBootstrap
    {
        public int WorkersWhenItRan { get; private set; } = -1;

        public async Task RunAsync(CancellationToken ct) => WorkersWhenItRan = await CountAsync(connectionString, "workers", ct);
    }

    private static async Task<int> CountAsync(string connectionString, string table, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM main.{table} t JOIN main.namespaces n ON n.id = t.namespace_id WHERE n.name = @namespace;";
        command.Parameters.AddWithValue("@namespace", WorkerNamespace);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<WorkerStatusCode> WorkerStatusAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT w.status_code FROM main.workers w JOIN main.namespaces n ON n.id = w.namespace_id WHERE n.name = @namespace;";
        command.Parameters.AddWithValue("@namespace", WorkerNamespace);
        var status = await command.ExecuteScalarAsync(ct);
        Assert.NotNull(status);
        return (WorkerStatusCode)Convert.ToByte(status, CultureInfo.InvariantCulture);
    }
}
