# How to wrap every handler in your own middleware

An `IJobPipelineBehavior` wraps every handler call, the way middleware wraps a request.

- Section: Watch and control running work, step 14
- Program: `concepts/400-observability-and-alerts/401-pipeline-behavior` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/401-pipeline-behavior
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the behavior around the call to the handler

What stands before `next()` runs before the handler, and what stands after it runs after.

The lines that matter:

```csharp
var sw = Stopwatch.StartNew();
Console.WriteLine($"-> {context.JobName} #{context.JobId} starting");
var result = await next();
Console.WriteLine($"<- {context.JobName} #{context.JobId} done in {sw.ElapsedMilliseconds} ms");
return result;
```

In `pipeline-behavior.cs` (excerpt):

```csharp
public sealed record GreetUser(string Name);

public static class GreetUserJob
{
    [Job("greet-user")]
    public static async Task Handle(GreetUser input, CancellationToken ct)
    {
        await Task.Delay(100, ct);
        Console.WriteLine($"  Hi, {input.Name}!");
    }
}

// Wraps every handler invocation; first registered is outermost. Resolved per attempt, so it can
// take constructor dependencies (including the scoped JobContext).
public sealed class TimingBehavior : IJobPipelineBehavior
{
    public async ValueTask<JobHandlerOutput> InvokeAsync(
        object request,
        JobContext context,
        JobBehaviorDelegate next,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"-> {context.JobName} #{context.JobId} starting");
        var result = await next();
        Console.WriteLine($"<- {context.JobName} #{context.JobId} done in {sw.ElapsedMilliseconds} ms");
        return result;
    }
}
```

## 2. Register the behavior

The first one registered is the outermost.

The lines that matter:

```csharp
j.AddPipelineBehavior<TimingBehavior>();
```

In `pipeline-behavior.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.AddPipelineBehavior<TimingBehavior>();
    j.Run<PipelineBehaviorJobs>("pipeline-behavior");
});

using var host = builder.Build();
await host.StartAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/400-observability-and-alerts/401-pipeline-behavior
```

Its output must contain:

```text
-> greet-user #
  Hi, Sam!
<- greet-user #
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to choose how many events a job records](/learn/407-event-recording.md)

## More

- [The whole program](/learn/401-pipeline-behavior.program.md): every file as compiled, 67 lines.
