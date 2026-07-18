using Acta;
using Acta.Concepts.JobContract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobContractJobs>("job-contract");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// A generated compile-checked contract names the job; the namespace resolves from the registered manifest.
var outcome = await jobs.EnqueueAsync(JobContractJobs.Hello, new Hello("World"));
Console.WriteLine($"Enqueued {JobContractJobs.Hello} as {outcome.JobRef}.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.JobContract
{
    public sealed record Hello(string Name);

    public static class HelloJob
    {
        [Job("hello")]
        public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");
    }
}
