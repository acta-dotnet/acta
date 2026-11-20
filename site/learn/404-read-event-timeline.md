# How to read why a job did what it did

The append-only event ledger answers why a job did what it did. `ListEventsAsync` with `LineageRoot` reads the timeline of a job and of every child it started.

- Section: Watch and control running work, step 4
- Program: `learn/400-observability-and-alerts/404-read-event-timeline` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/404-read-event-timeline
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Read the timeline of the job and its children

Query the full lineage event timeline scoped to the lineage root (the parent job). Events are returned newest-first; reverse to print in chronological order. The parent starts a `collect` child that fails with "data origin unavailable", which is the reason the timeline shows.

The lines that matter:

```csharp
var eventsPage = await queries.Ledger.ListEventsAsync(new ListEventsQuery(LineageRoot: parent, PageSize: 100));
var events = eventsPage.Items.Reverse().ToList();
```

In `read-event-timeline.cs` (excerpt):

```csharp
Console.WriteLine($"parent finished with status: {finished.Status}");

Console.WriteLine("event timeline for the lineage:");

var eventsPage = await queries.Ledger.ListEventsAsync(new ListEventsQuery(LineageRoot: parent, PageSize: 100));
var events = eventsPage.Items.Reverse().ToList();
foreach (var e in events)
{
    var details = e.DetailsText is { } d ? $" {d}" : "";
    var msg = e.Message is { } m ? $" ({m})" : "";
    Console.WriteLine($"  [{e.JobRef}] {e.EventCode}{details}{msg}");
}
```

## 2. The job it acts on

`read-event-timeline.cs` (excerpt):

```csharp
// Parent starts a child that gathers data; the child fails deliberately to show a failure reason on the timeline.
[Job("run-report", MaxAttempts = 1)]
public static async Task HandleParent(RunReport input, JobContext context, CancellationToken ct)
{
    Console.WriteLine($"[run-report] starting data collection for {input.Period}");
    var collected = await context.CallChildAsync("collect", new CollectData(input.Period), ct);
    if (!collected.Succeeded)
    {
        await context.FailAsync($"data collection failed for {input.Period}", ct);
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/404-read-event-timeline
```

Its output must contain:

```text
parent finished with status: Failed
(data collection failed for q4-2024)
(data origin unavailable)
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A timeline starts at the first attempt. The enqueue is the job's row, `CreatedAtUtc`, not an event.
- A wait writes two events at one instant: `job.execution-yielded` and `job.suspended`. A sleep writes `job.execution-yielded` and `job.slept`.
- `new ListEventsQuery(LineageRoot: root.JobRef)` reads a whole tree's timeline in one query, children included.
- `"collect"` is how `run-report` finds the child it already started. A renamed child is new work.
- A parent and its children are each stored under their own job name. The names in `[Job("run-report")]` and `[Job("collect-data")]`, and the namespace in `Run<ReadEventTimelineJobs>("read-event-timeline")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `RunReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `run-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to list and page through jobs](/learn/409-operator-queries.md)

## More

- [The whole program](/learn/404-read-event-timeline.program.md): every file as compiled, 79 lines.
