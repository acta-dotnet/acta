# How to run two namespaces in one process

Each `Run` starts a worker for one namespace. Two of them in one process share the process and the database, and never each other's jobs.

- Section: Deploy and scale, step 2
- Program: `learn/700-topology-and-deployment/703-namespaces-in-one-process` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/703-namespaces-in-one-process
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Run a worker for each namespace

Each Run starts an independent worker owning a distinct namespace (own claim loop, lease identity, heartbeat); they share process and database but never each other's jobs.

The lines that matter:

```csharp
j.Run<NamespacesInOneProcessJobs>("orders-eu");
j.Run<NamespacesInOneProcessJobs>("orders-us");
```

In `namespaces-in-one-process.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.Run<NamespacesInOneProcessJobs>("orders-eu");
    j.Run<NamespacesInOneProcessJobs>("orders-us");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
```

## 2. Name the namespace when you enqueue

A request by name targets one namespace. An enqueue by type has to name the namespace in its options when two namespaces run the same job.

The lines that matter:

```csharp
await jobs.EnqueueAsync(
    new EnqueueJobRequest("orders-eu", "process-order", JobPayload.Json(new ProcessOrder($"EU-{i}"))),
    CancellationToken.None
);
```

In `namespaces-in-one-process.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

// A raw request targets a namespace directly. A typed enqueue of a job that two namespaces share has to name
// the namespace in its options.
for (var i = 1; i <= 3; i++)
{
    await jobs.EnqueueAsync(
        new EnqueueJobRequest("orders-eu", "process-order", JobPayload.Json(new ProcessOrder($"EU-{i}"))),
        CancellationToken.None
    );
    await jobs.EnqueueAsync(
        new EnqueueJobRequest("orders-us", "process-order", JobPayload.Json(new ProcessOrder($"US-{i}"))),
        CancellationToken.None
    );
}
```

## 3. The job it acts on

`namespaces-in-one-process.cs` (excerpt):

```csharp
public static class ProcessOrderJob
{
    [Job("process-order")]
    public static void Handle(ProcessOrder input) => Console.WriteLine($"processed {input.OrderId}");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/700-topology-and-deployment/703-namespaces-in-one-process
```

Its output must contain:

```text
processed EU-1
processed EU-2
processed EU-3
processed US-1
processed US-2
processed US-3
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("process-order")]` and the namespaces `"orders-eu"` and `"orders-us"` are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessOrder` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to restart a crashed app on SQLite and get its work back](/learn/706-sqlite-restart.md)

## More

- [The whole program](/learn/703-namespaces-in-one-process.program.md): every file as compiled, 60 lines.
