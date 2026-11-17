# How to enqueue from a process that runs no worker

`Reference<TManifest>()` lets a process enqueue a namespace's jobs by type without running a worker for it. `Run<TManifest>()` is the process that executes them.

- Section: Deploy and scale, step 1
- Program: `concepts/700-topology-and-deployment/701-enqueue-only-reference` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/701-enqueue-only-reference
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Reference the jobs in the process that only enqueues

`Reference` makes the jobs enqueueable by type and starts no claim loop, so this process never runs a handler.

The lines that matter:

```csharp
j.Reference<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
```

In `enqueue-only-reference.cs` (excerpt):

```csharp
// In production, these are two separate processes (see demos/ApiWorkerSplit).
// One process here so the rung is self-contained and runnable without a second terminal.

// Worker host: owns the namespace - declares its jobs, runs the claim loop, executes them.
var workerBuilder = Host.CreateApplicationBuilder(args);
workerBuilder.Services.UseActa(j =>
{
    j.UseLocalDatabase(workerBuilder.Configuration);
    j.Run<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
});
using var workerHost = workerBuilder.Build();
await workerHost.StartAsync();

Console.WriteLine("Worker host started: owns the namespace, runs the claim loop.");

// Frontend host: Reference only - no claim loop, so it never runs a handler. This demo keeps both hosts in one
// assembly; a real frontend references only the shared manifest and contracts.
// Any process that can reach the same database and knows the manifest type can enqueue.
var frontendBuilder = Host.CreateApplicationBuilder(args);
frontendBuilder.Services.UseActa(j =>
{
    j.UseLocalDatabase(frontendBuilder.Configuration);
    j.Reference<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
});
using var frontendHost = frontendBuilder.Build();
await frontendHost.StartAsync();

Console.WriteLine("Frontend host started: Reference only - no worker, so no handler runs here.");

// Enqueue from the frontend host; it can reach the job type through the reference.
var frontendJobs = frontendHost.Services.GetRequiredService<IJobs>();

// The job is a row in the shared database; the worker process claims it from there.
var handle = await frontendJobs.EnqueueAsync(new WelcomeUser("alice"));

Console.WriteLine($"Frontend enqueued job {handle.JobRef} (action={handle.Action})");

// WaitAsync returns as soon as the worker finishes the job; the frontend reads the same database.
Console.WriteLine("Waiting for worker to drain...");
await handle.WaitAsync();

var snapshot = await frontendJobs.GetAsync(handle);
Console.WriteLine($"Job status after worker drain: {snapshot!.Status}");

await frontendHost.StopAsync();
await workerHost.StopAsync();
```

## 2. Enqueue as usual

The job is a row in the shared database; the worker process claims it from there.

The lines that matter:

```csharp
var handle = await frontendJobs.EnqueueAsync(new WelcomeUser("alice"));
```

In `enqueue-only-reference.cs` (excerpt):

```csharp
// In production, these are two separate processes (see demos/ApiWorkerSplit).
// One process here so the rung is self-contained and runnable without a second terminal.

// Worker host: owns the namespace - declares its jobs, runs the claim loop, executes them.
var workerBuilder = Host.CreateApplicationBuilder(args);
workerBuilder.Services.UseActa(j =>
{
    j.UseLocalDatabase(workerBuilder.Configuration);
    j.Run<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
});
using var workerHost = workerBuilder.Build();
await workerHost.StartAsync();

Console.WriteLine("Worker host started: owns the namespace, runs the claim loop.");

// Frontend host: Reference only - no claim loop, so it never runs a handler. This demo keeps both hosts in one
// assembly; a real frontend references only the shared manifest and contracts.
// Any process that can reach the same database and knows the manifest type can enqueue.
var frontendBuilder = Host.CreateApplicationBuilder(args);
frontendBuilder.Services.UseActa(j =>
{
    j.UseLocalDatabase(frontendBuilder.Configuration);
    // `Reference` makes the jobs enqueueable by type and starts no claim loop, so this process never runs a handler.
    j.Reference<EnqueueOnlyReferenceJobs>("enqueue-only-reference");
});
using var frontendHost = frontendBuilder.Build();
await frontendHost.StartAsync();

Console.WriteLine("Frontend host started: Reference only - no worker, so no handler runs here.");

// Enqueue from the frontend host; it can reach the job type through the reference.
var frontendJobs = frontendHost.Services.GetRequiredService<IJobs>();

var handle = await frontendJobs.EnqueueAsync(new WelcomeUser("alice"));

Console.WriteLine($"Frontend enqueued job {handle.JobRef} (action={handle.Action})");

// WaitAsync returns as soon as the worker finishes the job; the frontend reads the same database.
Console.WriteLine("Waiting for worker to drain...");
await handle.WaitAsync();

var snapshot = await frontendJobs.GetAsync(handle);
Console.WriteLine($"Job status after worker drain: {snapshot!.Status}");

await frontendHost.StopAsync();
await workerHost.StopAsync();
```

## 3. The job it acts on

`enqueue-only-reference.cs` (excerpt):

```csharp
public sealed record WelcomeUser(string Username);

public static class WelcomeUserJob
{
    [Job("welcome-user")]
    public static void Handle(WelcomeUser input) => Console.WriteLine($"[worker] executed welcome-user for {input.Username}");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/700-topology-and-deployment/701-enqueue-only-reference
```

Its output must contain:

```text
[worker] executed welcome-user for alice
Job status after worker drain: Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to run two namespaces in one process](/learn/703-namespaces-in-one-process.md)

## More

- [The whole program](/learn/701-enqueue-only-reference.program.md): every file as compiled, 70 lines.
