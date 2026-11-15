using System.Diagnostics;
using Acta.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Testing;

/// <summary>
/// What a test meets first when it drives its own handlers: a handler's service that takes a logger or the application
/// lifetime builds although the test registered neither, and a drive of a job that is not due yet answers at once instead
/// of waiting out the retry budget meant for a transiently skipped claim.
/// </summary>
public sealed class ActaTestHostDriveTests
{
    [Fact]
    public async Task A_logger_and_a_lifetime_resolve_unregistered_and_a_job_not_yet_due_is_answered_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"acta-host-drive-{Guid.NewGuid():N}.db");
        try
        {
            var host = await StartAsync(dbPath, static _ => { }, ct);
            try
            {
                Assert.NotNull(host.Services.GetService<ILogger<ActaTestHostDriveTests>>());
                var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                Assert.False(lifetime.ApplicationStopping.CanBeCanceled);

                // The first drive sleeps the job for ten minutes; a later one finds nothing due and says so at once. The
                // fastest of up to three is judged against the five-second retry budget, which a drive that waits it out
                // spends on every trial.
                var job = await host.Jobs.EnqueueAsync(new EnqueueJobRequest("host-drive", "job-sleep-basic", JobPayload.None), ct);
                Assert.Equal(ActaRunStatus.Rearmed, await host.RunOnceAsync(job, ct));
                var budget = TimeSpan.FromSeconds(5);
                var fastest = TimeSpan.MaxValue;
                for (var trial = 0; trial < 3 && fastest >= budget; trial++)
                {
                    var drive = Stopwatch.StartNew();
                    Assert.Equal(ActaRunStatus.NothingClaimed, await host.RunOnceAsync(job, ct));
                    fastest = drive.Elapsed < fastest ? drive.Elapsed : fastest;
                }

                Assert.True(fastest < budget, $"every drive waited out the budget for a job due in ten minutes, fastest {fastest}");
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task A_lifetime_the_test_registers_is_the_one_the_host_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"acta-host-lifetime-{Guid.NewGuid():N}.db");
        var registered = new NeverStoppingLifetime();
        try
        {
            var host = await StartAsync(dbPath, s => s.AddSingleton<IHostApplicationLifetime>(registered), ct);
            try
            {
                Assert.Same(registered, host.Services.GetRequiredService<IHostApplicationLifetime>());
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    private static Task<IActaTestHost> StartAsync(string dbPath, Action<IServiceCollection> configureServices, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
        return ActaTestHost.StartAsync(
            (j, _) =>
            {
                j.UseSqlite(o =>
                {
                    o.ConnectionString = connectionString;
                    o.MigrateOnStartup = true;
                });
                j.Run<TestJobs.TestJobsManifest>("host-drive", ownerTeam: "test");
            },
            new ActaTestHostOptions
            {
                ConfigureServices = s =>
                {
                    s.Configure<JobsOptions>(o => o.RunSystemJobs = false);
                    configureServices(s);
                },
            },
            ct
        );
    }
}
