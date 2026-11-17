// How to find a job by its deduplication key
// `JobLookup.ByDeduplicationKey` finds a job by the key it was enqueued with, when you did not keep its ref.
using Acta;
using Acta.Learn.ReadByDeduplicationKey;
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

// learn: 1. Enqueue with a key you can build again later
// A typed enqueue stores its key exactly as given. This one puts the job's name in front with
// `DeduplicationKey.ForDefinition`; whatever key you choose, build it the same way where you look the job up.
var businessKey = "welcome:sam";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));

// learn: end

// learn: 2. Look the job up by that key
// The first argument is the namespace the job was enqueued in, the name given to `Run<...>`; the second is the key
// exactly as stored. The lookup composes nothing from a business key.
var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey("read-by-deduplication-key", finalKey));

// learn: end
Console.WriteLine($"Found job {snapshot!.JobRef} by deduplication key '{snapshot.DeduplicationKey}', status {snapshot.Status}.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.ReadByDeduplicationKey
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
