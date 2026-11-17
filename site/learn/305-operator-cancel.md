# How to cancel a running job from outside

`CancelAsync` on a job's handle cancels it from outside its handler. Any process that holds an `IJobs` can do it, and a running handler's `CancellationToken` trips.

- Section: Watch and control running work, step 6
- Program: `concepts/300-failure-and-recovery/305-operator-cancel` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/305-operator-cancel
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Cancel the job from outside

Operator cancel from outside the handler: any process holding an IJobs trips a running job's CancellationToken.

The lines that matter:

```csharp
var result = await handle.CancelAsync("operator aborted the export");
Console.WriteLine($"cancel result: {result.Action}");
```

In `operator-cancel.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new BigExport("export-1"));
await Task.Delay(500); // let it start running
Console.WriteLine($"status while running: {(await handle.GetStatusAsync())?.Status}");

var result = await handle.CancelAsync("operator aborted the export");
Console.WriteLine($"cancel result: {result.Action}");

await handle.WaitAsync();
var snapshot = await jobs.GetAsync(handle);
Console.WriteLine($"final status: {snapshot!.Status}");

await host.StopAsync();
```

## 2. Pass the token to the work, so the cancel can stop it

The handler hands its CancellationToken to everything it awaits; the cancel from outside ends the wait here.

The lines that matter:

```csharp
await Task.Delay(TimeSpan.FromSeconds(30), ct); // operator cancels mid-run, so ct trips here
```

In `operator-cancel.cs` (excerpt):

```csharp
public sealed class BigExportJob
{
    [Job("big-export")]
    public async Task Handle(BigExport input, CancellationToken ct)
    {
        Console.WriteLine($"[{input.Id}] export started");
        await Task.Delay(TimeSpan.FromSeconds(30), ct); // operator cancels mid-run, so ct trips here
        Console.WriteLine($"[{input.Id}] export finished"); // never reached
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/300-failure-and-recovery/305-operator-cancel
```

Its output must contain:

```text
[export-1] export started
cancel result: Applied
final status: Cancelled
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to pause and resume a job from outside](/learn/309-operator-resume.md)

## More

- [The whole program](/learn/305-operator-cancel.program.md): every file as compiled, 56 lines.
