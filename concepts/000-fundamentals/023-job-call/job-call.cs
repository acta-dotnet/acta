using Acta;
using Acta.Concepts.JobCall;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobCallJobs>("job-call");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The manifest's generated call names the job and takes its handler's parameters; the namespace resolves from the
// registered manifest, and nothing is inferred from the input's type.
var single = await jobs.EnqueueAsync(JobCallJobs.Hello(new Hello("World")));
var several = await jobs.EnqueueAsync(JobCallJobs.Greet("Ada", 3), o => o.DeduplicationKey("greet-ada"));
Console.WriteLine($"Enqueued hello as {single.JobRef} and greet as {several.JobRef}.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.JobCall
{
    public sealed record Hello(string Name);

    public static class HelloJobs
    {
        [Job("hello")]
        public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");

        // Several input parameters travel as one generated record, JobCallJobs.GreetArgs, stored as {"name":..,"times":..}.
        [Job("greet")]
        public static void Greet(string name, int times)
        {
            for (var i = 0; i < times; i++)
            {
                Console.WriteLine($"Greetings, {name}!");
            }
        }
    }
}
