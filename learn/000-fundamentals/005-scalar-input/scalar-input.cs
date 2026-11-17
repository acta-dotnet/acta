// How to pass one plain value as the input
// The input can be one plain value, such as a string or a number, with no record around it.
using Acta;
using Acta.Learn.ScalarInput;
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

// learn: 2. Enqueue it by its generated call
// The generated call names the job, and a plain scalar works as its input; no record needed.
await jobs.EnqueueAsync(ScalarInputJobs.Shout("durable jobs are simple"));

// learn: end
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.ScalarInput
{
    public static class ShoutJob
    {
        // learn: 1. Take the plain value as the handler's parameter
        [Job("shout")]
        public static void Handle(string message) => Console.WriteLine(message.ToUpperInvariant());
        // learn: end
    }
}
