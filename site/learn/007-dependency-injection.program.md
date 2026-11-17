# The whole program: use your own services in a handler

The how-to is [/learn/007-dependency-injection.md](/learn/007-dependency-injection.md). This file holds every source file of `concepts/000-fundamentals/007-dependency-injection` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### dependency-injection.cs

```csharp
// How to use your own services in a handler
// The class that holds the handler takes its services in its constructor, from the application's own container.
using Acta;
using Acta.Concepts.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// learn: 1. Register the service as you would for any other code
builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

// learn: end

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

    // learn: 2. Take it in the constructor of the handler's class
    // Acta constructs a fresh handler instance per attempt, injecting constructor dependencies.
    public sealed class WelcomeJob(IEmailSender sender)
    {
        [Job("send-welcome-email")]
        public async Task Handle(SendWelcomeEmail input, CancellationToken ct) =>
            await sender.SendAsync(input.Email, $"Welcome, {input.Name}!", ct);
    }
    // learn: end
}
```

