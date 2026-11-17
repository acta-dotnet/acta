# How to inspect a job from the command line

The app's own binary answers `jobs info`, `events`, `explain`, and the control verbs for any job, read from the same database.

- Section: Watch and control running work, step 1
- Program: `concepts/000-fundamentals/021-jobs-cli` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/021-jobs-cli
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Run the app's own binary with a jobs verb

A first argument of `jobs` runs that verb against the app's database and exits; without it the process starts as a normal worker. The program needs no line of its own for this.

`jobs-cli.cs` (excerpt):

```csharp
const string cli = "dotnet run --project concepts/000-fundamentals/021-jobs-cli -- jobs";
Console.WriteLine($"Enqueued signal-waiting job {enqueued.JobRef}. From another terminal, try:");
Console.WriteLine($"  {cli} info {enqueued.JobRef}");
Console.WriteLine($"  {cli} events {enqueued.JobRef}");
Console.WriteLine($"  {cli} explain {enqueued.JobRef}");
Console.WriteLine($"  {cli} pause {enqueued.JobRef} --reason \"hold for review\"");
Console.WriteLine($"  {cli} resume {enqueued.JobRef}");
Console.WriteLine($"  {cli} restart {enqueued.JobRef}");
Console.WriteLine($"  {cli} debug {enqueued.JobRef}");
Console.WriteLine($"  {cli} info    (no target: a read takes the copied ref from the clipboard)");
```

## 2. The job it acts on

`jobs-cli.cs` (excerpt):

```csharp
public static class ReviewDocumentJob
{
    [Job("review-document")]
    public static async Task Handle(ReviewDocument input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine($"[{input.DocumentId}] indexed; waiting for legal approval");
        await context.WaitSignalAsync("legal-approval", ct);
        Console.WriteLine($"[{input.DocumentId}] approved");
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/021-jobs-cli
```

Its output must contain:

```text
[contract-42] indexed; waiting for legal approval
| suspended
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The command runs against the app's own database and exits without starting a worker. Code before `app.Run` still runs, so guard host-only startup work with `if (args is not ["jobs", ..])`.
- Start with `explain` for anything stuck, waiting, or failed.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to see your jobs in the dashboard](/learn/022-dashboard.md)

## More

- [The whole program and its notes](/learn/021-jobs-cli.program.md): every file as compiled, 94 lines, and the notes on the design, the rows to inspect, and how to break it.
