// How to run your first job
// One `[Job]` method, one enqueue, and a worker that runs it.
using Acta;
using Acta.Concepts.HelloActa;
using Acta.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    // learn: 2. Register Acta and start a worker
    // `UseSqlite` says where the job rows live: one file, no server. Swap it for `UsePostgres` or `UseSqlServer`, with
    // that server's connection string, to target a server; later rungs fold this setup into `UseLocalDatabase`.
    // `Run<HelloActaJobs>("hello-acta")` registers every job of the project under the namespace `hello-acta` and
    // starts a worker for it. `HelloActaJobs` is generated at build time from the `[Job]` methods.
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString = builder.Configuration.GetConnectionString("acta")
            ?? $"Data Source={Path.Combine(Path.GetTempPath(), "acta-local.db")}";
        sqlite.MigrateOnStartup = true;
    });
    j.Run<HelloActaJobs>("hello-acta");
    // learn: end
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: 3. Enqueue it
// `EnqueueAsync` writes the job as a row and returns. The type of the input picks the job, so there is no name
// string to get wrong.
await jobs.EnqueueAsync(new Hello("World"));

// learn: end
Console.WriteLine("Enqueued. The worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.HelloActa
{
    public sealed record Hello(string Name);

    public static class HelloJob
    {
        // learn: 1. Write the job
        // A job is a method with `[Job("name")]` on it, and the record is its input. Acta stores the input as a row
        // and calls the method with it.
        [Job("hello")]
        public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");
        // learn: end
    }
}
