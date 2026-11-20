# How to pass one plain value as the input

The input can be one plain value, such as a string or a number, with no record around it.

- Section: Start jobs, step 5
- Program: `learn/000-fundamentals/005-scalar-input` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/005-scalar-input
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Take the plain value as the handler's parameter

The lines that matter:

```csharp
[Job("shout")]
public static void Handle(string message) => Console.WriteLine(message.ToUpperInvariant());
```

In `scalar-input.cs` (excerpt):

```csharp
public static class ShoutJob
{
    [Job("shout")]
    public static void Handle(string message) => Console.WriteLine(message.ToUpperInvariant());
}
```

## 2. Enqueue it by its generated call

The generated call names the job, and a plain scalar works as its input; no record needed.

The lines that matter:

```csharp
await jobs.EnqueueAsync(ScalarInputJobs.Shout("durable jobs are simple"));
```

In `scalar-input.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScalarInputJobs>("scalar-input");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(ScalarInputJobs.Shout("durable jobs are simple"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/005-scalar-input
```

Its output must contain:

```text
DURABLE JOBS ARE SIMPLE
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("shout")]` and the namespace in `Run<ScalarInputJobs>("scalar-input")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to keep several jobs in one class](/learn/006-many-jobs-one-class.md)

## More

- [The whole program](/learn/005-scalar-input.program.md): every file as compiled, 40 lines.
