# Quickstart

One `[Job]`, one handler, one enqueue, one row you can `SELECT`. Everything here runs on embedded
SQLite: no Docker, no database server.

## Start in your own app

Three commands from an empty folder. The only prerequisite is the .NET 10 SDK.

```bash
dotnet new console -n Shipping && cd Shipping
dotnet add package Acta.Sqlite --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

A single provider package reference delivers everything: the runtime, the `[Job]` source generator,
and the analyzers. `Microsoft.Extensions.Hosting` is the second reference because Acta itself depends
only on `Hosting.Abstractions`; a Worker Service or ASP.NET Core project already has it.

Replace `Program.cs` with this, all of it:

```csharp
using Shipping;                 // the generated manifest lands in your project's root namespace
using Acta;
using Acta.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString = "Data Source=acta-local.db";
        sqlite.ApplyMigrationsOnStartup = true;   // dev convenience; run from a deploy step in production
    });
    j.Run<ShippingJobs>("shipping");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
await jobs.EnqueueAsync(new ShipOrder(1042));

Console.WriteLine("Enqueued. Ctrl+C to stop.");
await host.WaitForShutdownAsync();

public sealed record ShipOrder(int OrderId);

public static class ShippingHandlers
{
    [Job("ship-order")]
    public static void Handle(ShipOrder input) => Console.WriteLine($"Shipping order {input.OrderId}");
}
```

```bash
dotnet run
# Enqueued. Ctrl+C to stop.
# Shipping order 1042
```

The program enqueues a job, a worker in the same process claims and runs it, and `acta-local.db`
now holds the row. `Ctrl+C` stops it.

> **The `using Shipping;` line is not decoration.** The manifest is generated into your project's
> root namespace, while top-level statements live in the global namespace, so without it
> `ShippingJobs` will not resolve.

## Or explore the repository

88 runnable concept projects and the load-and-failure lab, if you would rather read the model first:

```bash
git clone https://github.com/acta-dotnet/acta && cd acta
dotnet run --project concepts/000-fundamentals/001-hello-acta
dotnet run --project anvil/Anvil
# Acta dashboard at http://127.0.0.1:5059/acta (the root URL is Anvil's own lab UI)
```

The dashboard UI is built by npm during the .NET build, so it needs Node.js 20.19+ or 22.12+ on PATH once;
without Node the lab and every API still run, and the dashboard route explains what is missing.

## The whole program

Three files make a complete host: the contract and handler, a supporting service, and the wiring.

```csharp
// File: Users/Jobs/SendWelcomeEmail.cs
using Acta;

namespace Users.Jobs;

public sealed record SendWelcomeEmail(Guid UserId, string Email, string DisplayName);

public sealed class UserJobs(EmailService email)
{
    [Job("send-welcome-email")]
    public Task Handle(SendWelcomeEmail request, CancellationToken ct)
        => email.SendWelcomeAsync(request.UserId, request.Email, request.DisplayName, ct);
}
```

```csharp
// File: Users/EmailService.cs
namespace Users;

public sealed class EmailService
{
    public Task SendWelcomeAsync(Guid userId, string email, string displayName, CancellationToken ct)
        => Task.CompletedTask;   // stand-in; wire your real sender here
}
```

```csharp
// File: Users/Program.cs
using Acta;
using Users;
using Users.Jobs;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<EmailService>();

builder.Services.UseActa(j =>
{
    j.UseSqlite(opts =>
    {
        opts.ConnectionString = "Data Source=acta-local.db";
        opts.ApplyMigrationsOnStartup = true;   // dev convenience; apply from a deploy step in production
    });
    j.Run<UsersJobs>(namespaceName: "users", ownerTeam: "growth");
});

var app = builder.Build();

app.MapPost("/signup", async (SignupForm form, IJobs jobs, CancellationToken ct) =>
{
    await jobs.EnqueueAsync(new SendWelcomeEmail(Guid.CreateVersion7(), form.Email, form.DisplayName), ct: ct);
    return Results.Accepted();
});

app.Run();

public sealed record SignupForm(string Email, string DisplayName);
```

What to know about these lines:

- **`[Job("send-welcome-email")]` is required and kebab-case.** That string is the durable,
  operator-facing contract used in SQL, the dashboard, the CLI, and alerts.
- **The class is not a framework concept.** Any class can host `[Job]` methods; handlers resolve
  through DI, so `EmailService` must be registered.
- **`UsersJobs` is the source-generated manifest** for the project's root namespace (`Users`): the
  last segment of `RootNamespace` plus `Jobs`. Register it once with `Run<UsersJobs>(...)`; one
  worker runtime owns one namespace. It is generated *into* that root namespace, so a file in a
  different namespace needs `using Users;` to see it.
- **Enqueue is type-driven.** The input record's type alone routes the call; one `EnqueueAsync` for
  every job you have.
- **The same binary is the worker.** The host's `IHostedService` wiring runs the claim loop; there
  is no separate worker process to deploy unless you want one.
- Everything else is framework default policy: `MaxAttempts = 15`, exponential backoff
  `"1m..1d x2 ~10%"` (double each attempt from one minute, capped at one day, with 10% jitter),
  `5m` execution timeout, 90-day retention, alerts on failure.

To point the same code at a server, swap the provider registration: `j.UseSqlServer(...)` or
`j.UsePostgres(...)`. In your own project, a single `<PackageReference Include="Acta.Sqlite" />`
(or the provider you need) delivers everything: the runtime, the `[Job]` source generator, and the
analyzers. The demos under `demos/` are complete apps built exactly this way, from the published
packages alone.

## Inspect the row

```csharp
var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail(...), ct: ct);
var job     = await jobs.GetAsync(outcome, ct);   // JobDetail? with Status, attempts, timestamps
```

Or with SQL, because the state is rows:

```sql
SELECT job_id, job_ref, job_name, status, failure_count, created_at_utc
FROM   acta.jobs_view
WHERE  namespace = 'users'
ORDER  BY created_at_utc DESC;
```

The events view holds the append-only timeline per job: every transition, with actor and reason.

## Where to go next

- Idempotent enqueue with deduplication keys, durable steps, signals, sleeps, and child jobs:
  [Concepts](./guide/concepts.md) and the [Engineering Labs](./engineering-labs.md).
- Namespace naming, policy defaults, and production wiring: [Configuration](./guide/configuration.md)
  and the [Production guide](./guide/production.md).
- The full handler surface: [Handler contract](./guide/handler-contract.md).
