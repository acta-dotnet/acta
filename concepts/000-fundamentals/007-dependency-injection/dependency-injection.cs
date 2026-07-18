using Acta;
using Acta.Concepts.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DependencyInjectionJobs>("dependency-injection");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.DependencyInjection
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public interface IEmailSender
    {
        Task SendAsync(string to, string subject, CancellationToken ct);
    }

    public sealed class ConsoleEmailSender : IEmailSender
    {
        public Task SendAsync(string to, string subject, CancellationToken ct)
        {
            Console.WriteLine($"[email] to={to} subject=\"{subject}\"");
            return Task.CompletedTask;
        }
    }

    // Acta constructs a fresh handler instance per attempt, injecting constructor dependencies.
    public sealed class WelcomeJob(IEmailSender sender)
    {
        [Job("send-welcome-email")]
        public async Task Handle(SendWelcomeEmail input, CancellationToken ct) =>
            await sender.SendAsync(input.Email, $"Welcome, {input.Name}!", ct);
    }
}
