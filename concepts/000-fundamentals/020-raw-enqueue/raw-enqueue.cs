using Acta;
using Acta.Concepts.RawEnqueue;
using Acta.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<RawEnqueueJobs>("raw-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Raw request names (namespace, job) explicitly instead of inferring from the input type: for
// out-of-process callers without the manifest, or when the input type alone is ambiguous.
var request = new JobEnqueueRequest(
    JobNamespace: "raw-enqueue",
    JobName: "send-welcome-email",
    Input: JobPayload.Json(new SendWelcomeEmail("sam@example.com", "Sam"))
);

// The explicit CancellationToken picks the raw-request overload over the typed EnqueueAsync<T>.
var outcome = await jobs.EnqueueAsync(request, CancellationToken.None);
Console.WriteLine($"Enqueued job {outcome.JobRef} by name. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.RawEnqueue
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
