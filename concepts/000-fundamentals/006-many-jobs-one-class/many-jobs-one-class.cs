using Acta;
using Acta.Concepts.ManyJobsOneClass;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ManyJobsOneClassJobs>("many-jobs-one-class");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Acta routes each enqueue to a handler by its input type.
await jobs.EnqueueAsync<string>("acta"); // -> shout
await jobs.EnqueueAsync<int>(21); // -> double-it
await jobs.EnqueueAsync<double>(9.0); // -> halve
Console.WriteLine("Enqueued 3 jobs. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.ManyJobsOneClass
{
    // Each [Job] method in a class registers independently.
    public static class TextAndMath
    {
        [Job("shout")]
        public static void Shout(string text) => Console.WriteLine(text.ToUpperInvariant());

        [Job("double-it")]
        public static void Double(int n) => Console.WriteLine(n * 2);

        [Job("halve")]
        public static void Halve(double x) => Console.WriteLine(x / 2);
    }
}
