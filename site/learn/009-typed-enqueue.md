# How to enqueue by the input's type

`EnqueueAsync` takes the input alone: its type names the job, so there is no name string to get wrong.

- Section: Start jobs, step 1
- Program: `learn/000-fundamentals/009-typed-enqueue` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/009-typed-enqueue
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue the input and let its type pick the job

No job-name string: Acta picks the job from the input type. The handle that comes back carries the job's ref and says whether this call inserted the job.

The lines that matter:

```csharp
var handle = await jobs.EnqueueAsync(new GreetUser("Sam"));
```

In `typed-enqueue.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TypedEnqueueJobs>("typed-enqueue");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new GreetUser("Sam"));
Console.WriteLine($"Enqueued job {handle.JobRef} ({handle.Action}). Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 2. The job it acts on

`typed-enqueue.cs` (excerpt):

```csharp
public static class GreetUserJob
{
    [Job("greet-user")]
    public static void Handle(GreetUser input) => Console.WriteLine($"Hi, {input.Name}!");
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/009-typed-enqueue
```

Its output must contain:

```text
(Inserted)
Hi, Sam!
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to enqueue by the job's generated call](/learn/023-job-call.md)

## More

- [The whole program](/learn/009-typed-enqueue.program.md): every file as compiled, 41 lines.
