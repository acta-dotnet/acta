# How to pass typed input to a job

The input is a record: Acta stores it with the job and hands it to the handler as its first parameter.

- Section: Take the first steps, step 2
- Program: `concepts/000-fundamentals/002-job-input` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/002-job-input
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the input as a record

The input is plain data. Acta serializes it, stores it with the job, and hands it to the handler.

The lines that matter:

```csharp
public sealed record PrintNameTag(string Name, string Workshop);
```

In `job-input.cs` (excerpt):

```csharp
public sealed record PrintNameTag(string Name, string Workshop);

public static class PrintNameTagJob
{
    [Job("print-name-tag")]
    public static async Task Handle(PrintNameTag tag, CancellationToken ct)
    {
        await Task.Delay(50, ct);
        Console.WriteLine($"Printed name tag: {tag.Name} ({tag.Workshop})");
    }
}
```

## 2. Enqueue an instance of it

The type of the value picks the job: the handler whose first parameter is a `PrintNameTag`.

The lines that matter:

```csharp
await jobs.EnqueueAsync(new PrintNameTag("Ada", "workshop"));
```

In `job-input.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new PrintNameTag("Ada", "workshop"));

Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/002-job-input
```

Its output must contain:

```text
Printed name tag: Ada (workshop)
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to return a result and wait for it](/learn/003-job-result.md)

## More

- [The whole program](/learn/002-job-input.program.md): every file as compiled, 48 lines.
