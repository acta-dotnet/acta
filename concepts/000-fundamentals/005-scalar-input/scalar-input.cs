using Acta;
using Acta.Concepts.ScalarInput;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScalarInputJobs>("scalar-input");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// A plain scalar works as input; no record needed.
await jobs.EnqueueAsync<string>("durable jobs are simple");
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.ScalarInput
{
    public static class ShoutJob
    {
        [Job("shout")]
        public static void Handle(string message) => Console.WriteLine(message.ToUpperInvariant());
    }
}
