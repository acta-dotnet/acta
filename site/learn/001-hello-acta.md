# How to run your first job

One `[Job]` method, one enqueue, and a worker that runs it.

- Section: Take the first steps, step 1
- Program: `concepts/000-fundamentals/001-hello-acta` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/001-hello-acta
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the job

A job is a method with `[Job("name")]` on it, and the record is its input. Acta stores the input as a row and calls the method with it.

The lines that matter:

```csharp
[Job("hello")]
public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");
```

In `hello-acta.cs` (excerpt):

```csharp
public static class HelloJob
{
    [Job("hello")]
    public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");
}
```

## 2. Register Acta and start a worker

`UseSqlite` says where the job rows live: one file, no server. Swap it for `UsePostgres` or `UseSqlServer`, with that server's connection string, to target a server; later rungs fold this setup into `UseLocalDatabase`. `Run<HelloActaJobs>("hello-acta")` registers every job of the project under the namespace `hello-acta` and starts a worker for it. `HelloActaJobs` is generated at build time from the `[Job]` methods.

The lines that matter:

```csharp
j.UseSqlite(sqlite =>
{
    sqlite.ConnectionString = builder.Configuration.GetConnectionString("acta")
        ?? $"Data Source={Path.Combine(Path.GetTempPath(), "acta-local.db")}";
    sqlite.MigrateOnStartup = true;
});
j.Run<HelloActaJobs>("hello-acta");
```

In `hello-acta.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString = builder.Configuration.GetConnectionString("acta")
            ?? $"Data Source={Path.Combine(Path.GetTempPath(), "acta-local.db")}";
        sqlite.MigrateOnStartup = true;
    });
    j.Run<HelloActaJobs>("hello-acta");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
```

## 3. Enqueue it

`EnqueueAsync` writes the job as a row and returns. The type of the input picks the job, so there is no name string to get wrong.

The lines that matter:

```csharp
await jobs.EnqueueAsync(new Hello("World"));
```

In `hello-acta.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new Hello("World"));
Console.WriteLine("Enqueued. The worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/001-hello-acta
```

Its output must contain:

```text
Hello, World!
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("hello")]` goes into the database, the dashboard, and alerts. Keep it lowercase with dashes, and treat it as permanent once jobs exist under it.
- `MigrateOnStartup = true` is for your own machine. In production a deploy step runs `jobs migrate`.
- The process that enqueues can also run the job. You can split the two later without changing the handler.

## Check yourself

1. `EnqueueAsync` has returned and the worker has not picked the job up yet. Where is the job?
   - a) In the memory of the running process
   - b) In a row of the SQLite file
   - c) In a separate queue service
   - Answer: b. The job is a durable row before anything runs it. Stop the process now and the row is still there for the next start.

## Next

- [How to pass typed input to a job](/learn/002-job-input.md)

## More

- [The whole program](/learn/001-hello-acta.program.md): every file as compiled, 58 lines.
