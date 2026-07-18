using System.Diagnostics;
using Acta;
using Acta.Concepts.PipelineBehavior;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.AddPipelineBehavior<TimingBehavior>();
    j.Run<PipelineBehaviorJobs>("pipeline-behavior");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new GreetUser("Sam"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.PipelineBehavior
{
    public sealed record GreetUser(string Name);

    public static class GreetUserJob
    {
        [Job("greet-user")]
        public static async Task Handle(GreetUser input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            Console.WriteLine($"  Hi, {input.Name}!");
        }
    }

    // Wraps every handler invocation; first registered is outermost. Resolved per attempt, so it can
    // take constructor dependencies (including the scoped JobContext).
    public sealed class TimingBehavior : IJobPipelineBehavior
    {
        public async ValueTask<JobHandlerInvocationResult> InvokeAsync(
            object request,
            JobContext context,
            JobBehaviorDelegate next,
            CancellationToken ct
        )
        {
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"-> {context.JobName} #{context.JobId} starting");
            var result = await next();
            Console.WriteLine($"<- {context.JobName} #{context.JobId} done in {sw.ElapsedMilliseconds} ms");
            return result;
        }
    }
}
