using Acta;
using Acta.Concepts.ReadByDeduplicationKey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ReadByDeduplicationKeyJobs>("read-by-deduplication-key");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Typed options accept an exact final key; compose the definition-qualified key explicitly.
var businessKey = "welcome:sam";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));

// Lookup uses the exact stored final key; it does not compose a definition name from a business key.
var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey("read-by-deduplication-key", finalKey));
Console.WriteLine($"Found job {snapshot!.JobRef} by deduplication key '{snapshot.DeduplicationKey}', status {snapshot.Status}.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.ReadByDeduplicationKey
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
