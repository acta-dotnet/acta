# How to write a job that takes no input

A job with no input: a handler with no parameters, enqueued by its generated call.

- Section: Start jobs, step 4
- Program: `learn/000-fundamentals/004-no-input-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/004-no-input-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write a handler with no parameters

With no input there is no type to enqueue by, so the job is named by the call the manifest generates for it.

The lines that matter:

```csharp
[Job("heartbeat")]
public static Task Handle()
{
    Console.WriteLine("Heartbeat tick (no input needed).");
    return Task.CompletedTask;
}
```

In `no-input-job.cs` (excerpt):

```csharp
public static class HeartbeatJob
{
    [Job("heartbeat")]
    public static Task Handle()
    {
        Console.WriteLine("Heartbeat tick (no input needed).");
        return Task.CompletedTask;
    }
}
```

## 2. Enqueue it by its generated call

The manifest's generated call names the job; a job without input takes no payload.

The lines that matter:

```csharp
await jobs.EnqueueAsync(NoInputJobJobs.Heartbeat());
```

In `no-input-job.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<NoInputJobJobs>("no-input-job");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(NoInputJobJobs.Heartbeat());
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/004-no-input-job
```

Its output must contain:

```text
Heartbeat tick (no input needed).
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to pass one plain value as the input](/learn/005-scalar-input.md)

## More

- [The whole program](/learn/004-no-input-job.program.md): every file as compiled, 45 lines.
