# How to keep several jobs in one class

A class can hold several `[Job]` methods. Each is a job of its own, with its own generated call.

- Section: Start jobs, step 6
- Program: `concepts/000-fundamentals/006-many-jobs-one-class` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/006-many-jobs-one-class
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Put the jobs in one class

Each [Job] method in a class registers independently, under the name in its attribute.

The lines that matter:

```csharp
[Job("shout")]
public static void Shout(string text) => Console.WriteLine(text.ToUpperInvariant());

[Job("double-it")]
public static void Double(int n) => Console.WriteLine(n * 2);

[Job("halve")]
public static void Halve(double x) => Console.WriteLine(x / 2);
```

In `many-jobs-one-class.cs` (excerpt):

```csharp
public static class TextAndMath
{
    [Job("shout")]
    public static void Shout(string text) => Console.WriteLine(text.ToUpperInvariant());

    [Job("double-it")]
    public static void Double(int n) => Console.WriteLine(n * 2);

    [Job("halve")]
    public static void Halve(double x) => Console.WriteLine(x / 2);
}
```

## 2. Enqueue each job by its own call

Each [Job] method gets its own generated call, named after the job.

The lines that matter:

```csharp
await jobs.EnqueueAsync(ManyJobsOneClassJobs.Shout("acta"));
await jobs.EnqueueAsync(ManyJobsOneClassJobs.DoubleIt(21));
await jobs.EnqueueAsync(ManyJobsOneClassJobs.Halve(9.0));
```

In `many-jobs-one-class.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(ManyJobsOneClassJobs.Shout("acta"));
await jobs.EnqueueAsync(ManyJobsOneClassJobs.DoubleIt(21));
await jobs.EnqueueAsync(ManyJobsOneClassJobs.Halve(9.0));
Console.WriteLine("Enqueued 3 jobs. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/006-many-jobs-one-class
```

Its output must contain:

```text
ACTA
42
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to run a job once, later](/learn/011-delayed-job.md)

## More

- [The whole program](/learn/006-many-jobs-one-class.program.md): every file as compiled, 49 lines.
