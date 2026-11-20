# How to read a job's result

`TryGetResultAsync<T>` finds nothing before Succeeded; `GetResultAsync<T>` returns the result you expect.

- Section: Take the first steps, step 6
- Program: `learn/000-fundamentals/013-read-result` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/013-read-result
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Ask whether a result exists yet

Both reads are point-in-time and never wait. `TryGetResultAsync` says whether a result exists yet.

The lines that matter:

```csharp
var early = await handle.TryGetResultAsync<AddNumbersResult>();
Console.WriteLine($"right away:        {(early.Found ? early.Value!.Sum.ToString() : "no result yet")}");
```

In `read-result.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new AddNumbers(20, 22));

var early = await handle.TryGetResultAsync<AddNumbersResult>();
Console.WriteLine($"right away:        {(early.Found ? early.Value!.Sum.ToString() : "no result yet")}");

await handle.WaitAsync();

var done = await handle.GetResultAsync<AddNumbersResult>();
Console.WriteLine($"after it finishes: {done.Sum}");
```

## 2. Read the result you expect

`GetResultAsync` returns the result, and throws when there is none.

The lines that matter:

```csharp
var done = await handle.GetResultAsync<AddNumbersResult>();
Console.WriteLine($"after it finishes: {done.Sum}");
```

In `read-result.cs` (excerpt):

```csharp
var early = await handle.TryGetResultAsync<AddNumbersResult>();
Console.WriteLine($"right away:        {(early.Found ? early.Value!.Sum.ToString() : "no result yet")}");

await handle.WaitAsync();

var done = await handle.GetResultAsync<AddNumbersResult>();
Console.WriteLine($"after it finishes: {done.Sum}");

await host.StopAsync();
```

## 3. The job it acts on

`read-result.cs` (excerpt):

```csharp
public static class AddNumbersJob
{
    [Job("add-numbers")]
    public static async Task<AddNumbersResult> Handle(AddNumbers input, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        return new AddNumbersResult(input.Left + input.Right);
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/013-read-result
```

Its output must contain:

```text
right away:        no result yet
after it finishes: 42
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `GetResultAsync<T>` throws when there is no result; `TryGetResultAsync<T>` answers `(Found, Value)`. Neither read waits for the job.
- The name in `[Job("add-numbers")]` and the namespace in `Run<ReadResultJobs>("read-result")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `AddNumbers` is stored with each job as its input, and `AddNumbersResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `add-numbers-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to start a job from an HTTP endpoint and poll it](/learn/024-aspnet-enqueue-api.md)

## More

- [The whole program](/learn/013-read-result.program.md): every file as compiled, 56 lines.
