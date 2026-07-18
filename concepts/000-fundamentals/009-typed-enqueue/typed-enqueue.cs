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

// No job-name string: Acta picks the job from the input type.
var outcome = await jobs.EnqueueAsync(new GreetUser("Sam"));
Console.WriteLine($"Enqueued job {outcome.JobRef} ({outcome.Action}). Worker is running - press Ctrl+C to stop.");

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
