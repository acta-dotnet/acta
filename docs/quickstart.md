# Quickstart

One `[Job]`, one handler, one enqueue, one row you can `SELECT`. Everything here runs on embedded
SQLite: no Docker, no database server.

## Run it from the repository

The preview is source-first: clone and run.

```bash
git clone https://github.com/acta-dotnet/acta && cd acta
dotnet run --project concepts/000-fundamentals/001-hello-acta
```

The program enqueues a job, a worker in the same process claims and runs it, and the console shows
the result. `Ctrl+C` stops it. The dashboard comes from the bundled lab host:

```bash
dotnet run --project anvil/Anvil
# Acta dashboard at http://127.0.0.1:5059/acta/jobs (the root URL is Anvil's own lab UI)
```

The dashboard UI is built by npm during the .NET build, so it needs Node.js 20+ on PATH once;
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
- **`UsersJobs` is the source-generated manifest** for the project's root namespace (`Users`).
  Register it once with `Run<UsersJobs>(...)`; one worker runtime owns one namespace.
- **Enqueue is type-driven.** The input record's type alone routes the call; one `EnqueueAsync` for
  every job you have.
- **The same binary is the worker.** The host's `IHostedService` wiring runs the claim loop; there
  is no separate worker process to deploy unless you want one.
- Everything else is framework default policy: `MaxAttempts = 15`, exponential backoff `"1m..8h"`,
  `5m` execution timeout, 90-day retention, alerts on failure.

To point the same code at a server, swap the provider registration: `j.UseSqlServer(...)` or
`j.UsePostgres(...)`. While the preview is source-only, reference the provider with a
`<ProjectReference>` to `src/Acta.Sqlite` (or the provider you need); when packages are published, a
single `<PackageReference Include="Acta.Sqlite" />` replaces it.

## Inspect the row

```csharp
var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail(...), ct: ct);
var job     = await jobs.GetAsync(outcome, ct);   // JobSnapshot? with Status, attempts, timestamps
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
