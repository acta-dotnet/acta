# The whole program: inspect a job from the command line

The how-to is [/learn/021-jobs-cli.md](/learn/021-jobs-cli.md). This file holds every source file of `learn/000-fundamentals/021-jobs-cli` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### jobs-cli.cs

```csharp
// How to inspect a job from the command line
// The app's own binary answers `jobs info`, `events`, `explain`, and the control verbs for any job, read from the
// same database.
using Acta;
using Acta.Labs;
using Acta.Learn.JobsCli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobsCliJobs>("jobs-cli");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// A first argument of "jobs" runs that control verb and exits before this line; otherwise the
// process starts as a normal worker.
var enqueued = await jobs.EnqueueAsync(new ReviewDocument("contract-42"));
using var suspensionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (true)
{
    var snapshot = await jobs.GetAsync(enqueued, suspensionTimeout.Token);
    if (snapshot?.Status == JobStatusCode.Suspended)
    {
        break;
    }
    if (snapshot?.Status.IsFinished == true)
    {
        throw new InvalidOperationException($"CLI fixture {snapshot.JobRef} ended {snapshot.Status} before reaching its signal wait.");
    }
    await Task.Delay(50, suspensionTimeout.Token);
}

// learn: Run the app's own binary with a jobs verb
// A first argument of `jobs` runs that verb against the app's database and exits; without it the process starts as
// a normal worker. The program needs no line of its own for this.
const string cli = "dotnet run --project learn/000-fundamentals/021-jobs-cli -- jobs";
Console.WriteLine($"Enqueued signal-waiting job {enqueued.JobRef}. From another terminal, try:");
Console.WriteLine($"  {cli} info {enqueued.JobRef}");
Console.WriteLine($"  {cli} events {enqueued.JobRef}");
Console.WriteLine($"  {cli} explain {enqueued.JobRef}");
Console.WriteLine($"  {cli} pause {enqueued.JobRef} --reason \"hold for review\"");
Console.WriteLine($"  {cli} resume {enqueued.JobRef}");
Console.WriteLine($"  {cli} restart {enqueued.JobRef}");
Console.WriteLine($"  {cli} debug {enqueued.JobRef}");
Console.WriteLine($"  {cli} info    (no target: a read takes the copied ref from the clipboard)");

// learn: end
await lab.ShowAllAsync(
    "Explore the complete signal-waiting job record",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "Direct SQL is the durable evidence behind info, events, and explain",
    """
    SELECT job_ref, job_status, execution_number, leased_by_worker_id
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.JobsCli
{
    public sealed record ReviewDocument(string DocumentId);

    public static class ReviewDocumentJob
    {
        [Job("review-document")]
        public static async Task Handle(ReviewDocument input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[{input.DocumentId}] indexed; waiting for legal approval");
            await context.WaitSignalAsync("legal-approval", ct);
            Console.WriteLine($"[{input.DocumentId}] approved");
        }
    }
}
```

## Notes

### The problem

A separate job-admin application can drift from the worker's manifest, dependency injection, provider,
and deployment version. A status alone also does not explain how the job arrived there or what an
operator should do next.

### Common approaches

- Build and deploy a separate administration tool.
- Operate only through a dashboard.
- Query SQL directly and interpret runtime codes by hand.
- Switch the application binary into a CLI mode using its real registration.

### Why this design

Starting the same app with `jobs ...` invokes Acta's embedded CLI before normal workload code. It uses
the app's actual namespace, manifest, and database. This lab leaves a job suspended on a signal so
`info`, `events`, `explain`, and `debug` have meaningful evidence to inspect.

### Trade-offs

Operators need access to the application artifact and its configuration/secrets. CLI availability is
tied to that deployment, and direct mutation still needs organizational authorization and audit policy.

### Run the experiment

Terminal 1:

```bash
dotnet run --project learn/000-fundamentals/021-jobs-cli
```

Use the printed job ref in terminal 2:

```bash
dotnet run --project learn/000-fundamentals/021-jobs-cli -- jobs info <job-ref>
dotnet run --project learn/000-fundamentals/021-jobs-cli -- jobs events <job-ref>
dotnet run --project learn/000-fundamentals/021-jobs-cli -- jobs explain <job-ref>
dotnet run --project learn/000-fundamentals/021-jobs-cli -- jobs debug <job-ref> --break
```

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`info` is the current snapshot, `events` is the path to it, `explain` interprets durable state and gives
operator guidance, and direct `jobs_view` SQL is the evidence below all three. The CLI and views are
operator surfaces; application code should normally call `IJobs`.

### Break it

Stop terminal 1. Run `info`, `events`, and `explain` again from the same configured store. Reads do not
need the original worker process. Compare that with `debug`, a mutation: it restarts a job that is not `Ready` (reason
`cli debug`, failure budget reset), then claims and executes it in-process. It refuses an `Executing`
job, and may report a laned job `Blocked` behind its lane's head instead of running it.

### When not to use

Prefer a centrally authorized control plane when operators cannot safely receive application secrets or
artifacts. Prefer the dashboard for broad visual triage and automation APIs for repeatable fleet actions.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [The explanation Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [The persisted-debugging Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`jobs-cli.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/000-fundamentals/021-jobs-cli/jobs-cli.cs)
- [`CliCommandRunner.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Cli/CliCommandRunner.cs)
- [`ExplainJobHandler.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Operations/Jobs/ExplainJob/ExplainJobHandler.cs)
