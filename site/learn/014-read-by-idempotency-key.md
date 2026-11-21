# How to find a job by its idempotency key

`JobLookup.ByIdempotencyKey` finds a job by the key it was enqueued with, when you did not keep its ref.

- Section: Start jobs, step 12
- Program: `learn/000-fundamentals/014-read-by-idempotency-key` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/014-read-by-idempotency-key
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue with a key you can build again later

A typed enqueue stores its key exactly as given. This one puts the job's name in front with `IdempotencyKey.ForDefinition`; whatever key you choose, build it the same way where you look the job up.

The lines that matter:

```csharp
var businessKey = "welcome:sam";
var finalKey = IdempotencyKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.IdempotencyKey(finalKey));
```

In `read-by-idempotency-key.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var businessKey = "welcome:sam";
var finalKey = IdempotencyKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.IdempotencyKey(finalKey));

var snapshot = await jobs.GetAsync(JobLookup.ByIdempotencyKey("read-by-idempotency-key", finalKey));
Console.WriteLine($"Found job {snapshot!.JobRef} by idempotency key '{snapshot.IdempotencyKey}', status {snapshot.Status}.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");
```

## 2. Look the job up by that key

The first argument is the namespace the job was enqueued in, the name given to `Run<...>`; the second is the key exactly as stored. The lookup composes nothing from a business key.

The lines that matter:

```csharp
var snapshot = await jobs.GetAsync(JobLookup.ByIdempotencyKey("read-by-idempotency-key", finalKey));
```

In `read-by-idempotency-key.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var businessKey = "welcome:sam";
var finalKey = IdempotencyKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.IdempotencyKey(finalKey));

var snapshot = await jobs.GetAsync(JobLookup.ByIdempotencyKey("read-by-idempotency-key", finalKey));
Console.WriteLine($"Found job {snapshot!.JobRef} by idempotency key '{snapshot.IdempotencyKey}', status {snapshot.Status}.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 3. The job it acts on

`read-by-idempotency-key.cs` (excerpt):

```csharp
public static class WelcomeJob
{
    [Job("send-welcome-email")]
    public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/014-read-by-idempotency-key
```

Its output must contain:

```text
by idempotency key 'send-welcome-email:welcome:sam'
Sent welcome email to Sam <sam@example.com>
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("send-welcome-email")]` and the namespace in `Run<ReadByIdempotencyKeyJobs>("read-by-idempotency-key")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to choose between starting a job and waiting for it](/learn/015-call-vs-enqueue.md)

## More

- [The whole program](/learn/014-read-by-idempotency-key.program.md): every file as compiled, 51 lines.
