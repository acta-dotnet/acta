// How to enqueue by the input's type
// `EnqueueAsync` takes the input alone: its type names the job, so there is no name string to get wrong.
using Acta;
using Acta.Concepts.TypedEnqueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TypedEnqueueJobs>("typed-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// learn: Enqueue the input and let its type pick the job
// No job-name string: Acta picks the job from the input type. The handle that comes back carries the job's ref and
// says whether this call inserted the job.
var handle = await jobs.EnqueueAsync(new GreetUser("Sam"));

// learn: end
Console.WriteLine($"Enqueued job {handle.JobRef} ({handle.Action}). Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.TypedEnqueue
{
    public sealed record GreetUser(string Name);

    public static class GreetUserJob
    {
        [Job("greet-user")]
        public static void Handle(GreetUser input) => Console.WriteLine($"Hi, {input.Name}!");
    }
}
