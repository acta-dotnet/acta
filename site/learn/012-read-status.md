# How to read a job's status

`GetAsync` answers a snapshot of the job: its ref, its name and namespace, its status, and when it was created.

- Section: Take the first steps, step 5
- Program: `learn/000-fundamentals/012-read-status` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/012-read-status
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Read the job with GetAsync

`GetAsync` returns a snapshot of the job's current state, or null when there is no such job. The handle that `EnqueueAsync` returned names the job; so does its `JobRef`.

The lines that matter:

```csharp
var snapshot = await jobs.GetAsync(handle);
Console.WriteLine($"job={snapshot!.JobRef} name={snapshot.JobName} ns={snapshot.JobNamespace}");
Console.WriteLine($"status={snapshot.Status} created={snapshot.CreatedAtUtc:O}");
```

In `read-status.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"));
await handle.WaitAsync();

var snapshot = await jobs.GetAsync(handle);
Console.WriteLine($"job={snapshot!.JobRef} name={snapshot.JobName} ns={snapshot.JobNamespace}");
Console.WriteLine($"status={snapshot.Status} created={snapshot.CreatedAtUtc:O}");

await host.StopAsync();
```

## 2. The job it acts on

`read-status.cs` (excerpt):

```csharp
public static class WelcomeJob
{
    [Job("send-welcome-email")]
    public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/012-read-status
```

Its output must contain:

```text
status=Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `ExecutionNumber` counts every claim of the job, each resume after a wait and each bounce off a full concurrency key included. Show `FailureCount` as the tries.
- A sleeping job reads `ready` with a future `NextRunAtUtc`; a job waiting for a signal or a child reads `suspended`. Both are healthy.
- `executing` means claimed, not that a process is alive: `LeaseLapsed` is true for an executing job whose worker stopped heartbeating.
- The text of a status is `job.Status.Code`, such as `suspended`. `ToString()` gives the C# name, `Suspended`.
- The name in `[Job("send-welcome-email")]` and the namespace in `Run<ReadStatusJobs>("read-status")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to read a job's result](/learn/013-read-result.md)

## More

- [The whole program](/learn/012-read-status.program.md): every file as compiled, 44 lines.
