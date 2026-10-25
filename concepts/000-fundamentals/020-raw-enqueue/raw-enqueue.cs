using Acta;
using Acta.Concepts.RawEnqueue;
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

// Raw request names (namespace, job) explicitly, for a caller without the manifest, such as a gateway relaying
// a request body. An in-process caller with an ambiguous input type uses the generated call instead (023).
var request = new EnqueueJobRequest(
    JobNamespace: "raw-enqueue",
    JobName: "send-welcome-email",
    Input: JobPayload.Json(new SendWelcomeEmail("sam@example.com", "Sam"))
);

// The explicit CancellationToken picks the raw-request overload over the typed EnqueueAsync<T>.
var enqueued = await jobs.EnqueueAsync(request, CancellationToken.None);
Console.WriteLine($"Enqueued job {enqueued.JobRef} by name. Worker is running - press Ctrl+C to stop.");

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
