# Quickstart

One `[Job]`, one handler, one enqueue, one row you can `SELECT`. Everything here runs on embedded
SQLite: no Docker, no database server.

## Start in your own app

Three commands from an empty folder. The only prerequisite is the .NET 10 SDK.

```bash
dotnet new web -n Shipping && cd Shipping
dotnet add package Acta.Sqlite --prerelease
dotnet add package Acta.AspNetCore --prerelease
```

A single provider package reference delivers everything: the runtime, the `[Job]` source generator,
and the analyzers. `Acta.AspNetCore` adds the embedded dashboard and JSON API — the built UI ships
inside the package, so there is no Node, no separate frontend, nothing to build.

Replace `Program.cs` with this, all of it:

```csharp
using Shipping;                 // the generated manifest lands in your project's root namespace
using Acta;
using Acta.AspNetCore;
using Acta.Sqlite;

var builder = WebApplication.CreateBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString = "Data Source=acta-local.db";
        sqlite.ApplyMigrationsOnStartup = true;   // local development only; apply from a deploy step in production
    });
    j.Run<ShippingJobs>("shipping");
});

var app = builder.Build();
app.MapActa("/acta");           // dashboard + JSON API; local-only by default, controls disabled

// Your own endpoint enqueues: the job is a durable row before the response goes out.
app.MapPost("/orders/{orderId:int}", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new ShipOrder(orderId));
    return Results.Accepted("/acta", new { job.JobRef });
});

app.Run();

public sealed record ShipOrder(int OrderId);

public static class ShippingHandlers
{
    [Job("ship-order")]
    public static void Handle(ShipOrder input) => Console.WriteLine($"Shipping order {input.OrderId}");
}
```

```bash
dotnet run
# then, in a second terminal, on the port dotnet run printed:
curl -X POST http://localhost:5090/orders/1042
# the app prints: Shipping order 1042
```

The endpoint returns `202 Accepted` with the job's ref as soon as the row is stored, and a worker in the same
process runs it. To run a job later or follow it to its outcome:

```csharp
// Run it later: one job, held until its time. A recurring schedule is [Schedule].
var reminder = await jobs.ScheduleAsync(new ShipOrder(1043), TimeSpan.FromMinutes(10));

// Follow a job: the handle waits for the outcome, and a timeout stops the wait, never the job.
var outcome = await reminder.WaitAsync(TimeSpan.FromSeconds(30));
```

`acta-local.db` holds the row. Open `/acta` on the same port: the Jobs screen lists `ship-order` as completed.

> **The `using Shipping;` line is not decoration.** The manifest is generated into your project's
> root namespace, while top-level statements live in the global namespace, so without it
> `ShippingJobs` will not resolve. The naming rule and the fix are in
> [Troubleshooting](./operator-guide.md#the-generated-manifest-type-does-not-resolve).

## No web host?

The same program runs on the generic host without the dashboard: `dotnet new console`, swap
`Acta.AspNetCore` for `Microsoft.Extensions.Hosting` (Acta itself depends only on
`Hosting.Abstractions`), and in `Program.cs` replace `using Acta.AspNetCore;` with
`using Microsoft.Extensions.DependencyInjection;` and `using Microsoft.Extensions.Hosting;` (a console
project's implicit usings cover neither), replace the builder lines with
`var builder = Host.CreateApplicationBuilder(args);` and `using var host = builder.Build();`, drop
the `MapActa` line, and replace the endpoint and `app.Run();` with an enqueue at startup:
`await host.StartAsync();`, `await host.Services.GetRequiredService<IJobs>().EnqueueAsync(new ShipOrder(1042));`, and
`await host.WaitForShutdownAsync();`. Everything durable — the rows, the retries, the CLI — is identical; only the dashboard
needs a web host.

## Or explore the repository

A ladder of runnable concept projects and the load-and-failure lab, if you would rather read the model first:

```bash
git clone https://github.com/acta-dotnet/acta && cd acta
dotnet run --project concepts/000-fundamentals/001-hello-acta
dotnet run --project anvil/Anvil
# Acta dashboard at http://127.0.0.1:5059/acta (the root URL is Anvil's own lab UI)
```

The dashboard UI is built by npm during the .NET build, so it needs Node.js 20.19+ or 22.12+ on PATH once;
without Node the lab and every API still run, and the dashboard route explains what is missing.

## The whole program

The same default grown into a real shape: three files — the contract and handler, a supporting
service resolved through DI, and the wiring — with the job enqueued from an endpoint instead of at
startup.

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

public class EmailService
{
    // Virtual so a test can stand in for it (see the testing guide).
    public virtual Task SendWelcomeAsync(Guid userId, string email, string displayName, CancellationToken ct)
        => Task.CompletedTask;   // stand-in; wire your real sender here
}
```

```csharp
// File: Users/Program.cs
using Acta;
using Acta.Sqlite;
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
- **Enqueue is typed.** `jobs.EnqueueAsync(new SendWelcomeEmail(...))` routes by the input record's
  type, and `UsersJobs.SendWelcomeEmail(...)` is the call that names the job outright; both answer a
  `JobHandle`.
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
var job = await jobs.EnqueueAsync(new SendWelcomeEmail(...), ct: ct);   // JobHandle
var row = await jobs.GetAsync(job, ct);                                 // GetJobResponse? with Status, attempts, timestamps
```

Or with SQL, because the state is rows. Open `acta-local.db` in any SQLite client; on PostgreSQL or SQL Server
the views sit in the configured schema, so the same query reads `acta.jobs_view`:

```sql
SELECT job_id, job_ref, job_name, status, failure_count, created_at_utc
FROM   jobs_view
WHERE  namespace = 'users'
ORDER  BY created_at_utc DESC;
```

The events view holds the append-only timeline per job: every transition, with actor and reason.

## Where to go next

- The same app through a job's whole life, typed result to first test: [Your first job, end to end](./first-job.md).
- Idempotent enqueue with deduplication keys, durable steps, signals, sleeps, and child jobs:
  [Concepts](./developer-guide.md#concepts-and-glossary) and the [Engineering Labs](./engineering-labs.md).
- Namespace naming, policy defaults, and production wiring: [Configuration](./developer-guide.md#configuration-and-options)
  and the [Production guide](./operator-guide.md#production-guide).
- The full handler surface: [Handler contract](./developer-guide.md#handler-contract).
