using Acta;
using Smoke;
#if SMOKE_TESTING
using Acta.Sqlite;
using Acta.Testing;
using Microsoft.Data.Sqlite;
#endif

// SmokeJobs only exists if the source generator ran from the package's analyzer assets.
Console.WriteLine($"generated manifest: {typeof(SmokeJobs).FullName}");

// OutboxProducer.Stage only compiles if the packed provider outbox staging extension resolved; its DDL
// source is pure string building, safe to run with no server.
Console.WriteLine($"outbox DDL bytes: {OutboxProducer.Ddl().Length}");

// One type reference per optional extra package: compiling proves the package restored with a
// resolvable dependency graph from the feed alone.
#if SMOKE_REDIS
Console.WriteLine($"redis package: {typeof(Acta.Redis.RedisActaBuilderExtensions).FullName}");
#endif
#if SMOKE_TESTING
// The packed runtime runs a job: run.ps1 layers this package on the SQLite provider, so the provider, the generated
// manifest and the test host migrate a file of their own and drive the hello job through it.
var database = Path.Combine(Path.GetTempPath(), $"acta-smoke-{Guid.NewGuid():N}.db");
await using (
    var host = await ActaTestHost.StartAsync(
        (j, _) =>
        {
            j.UseSqlite(sqlite =>
            {
                sqlite.ConnectionString = $"Data Source={database}";
                sqlite.MigrateOnStartup = true;
            });
            j.Run<SmokeJobs>("smoke");
        }
    )
)
{
    var job = await host.Jobs.EnqueueAsync(new Hello("packed"));
    var ran = await host.RunOnceAsync(job);
    // A cancelled run reports Completed too, so the handler's own word is what says it ran, on the input it was sent.
    if (ran != ActaRunStatus.Completed || HelloJob.Greeting != "hello packed")
    {
        throw new InvalidOperationException($"The packed runtime ran the hello job to {ran}, and its handler said '{HelloJob.Greeting}'.");
    }
}

SqliteConnection.ClearAllPools();
File.Delete(database);
Console.WriteLine("testing package: ran the hello job");
#endif
#if SMOKE_ASPNETCORE
Console.WriteLine($"aspnetcore package: {typeof(Acta.AspNetCore.ActaEndpointRouteBuilderExtensions).FullName}");
#endif
#if SMOKE_DASHBOARD
Console.WriteLine($"dashboard package: {typeof(Acta.Dashboard.ActaDashboardEndpointRouteBuilderExtensions).FullName}");
#endif
Console.WriteLine("package smoke OK");

namespace Smoke
{
    public sealed record Hello(string Name);

    public static class HelloJob
    {
        /// <summary>What the handler last printed.</summary>
        public static string? Greeting { get; private set; }

        [Job("hello")]
        public static void Handle(Hello input) => Console.WriteLine(Greeting = $"hello {input.Name}");
    }
}
