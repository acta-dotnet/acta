using Acta;
using Acta.Concepts.HelloActa;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    // 001 spells the setup out in full; later rungs fold it into j.UseLocalDatabase(builder.Configuration).
    // SQLite is the zero-setup default: one temp file, no server. Swap UseSqlite for UsePostgres or
    // UseSqlServer (connection string from ACTA_TEST_PG / ACTA_TEST_MSSQL) to target a real server.
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString =
            builder.Configuration.GetConnectionString("acta") ?? $"Data Source={Path.Combine(Path.GetTempPath(), "acta-local.db")}";
        sqlite.ApplyMigrationsOnStartup = true;
    });
    j.Run<HelloActaJobs>("hello-acta");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new Hello("World"));
Console.WriteLine("Enqueued. The worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.HelloActa
{
    public sealed record Hello(string Name);

    public static class HelloJob
    {
        [Job("hello")]
        public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");
    }
}
