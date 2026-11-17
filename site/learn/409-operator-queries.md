# How to list and page through jobs

`operations.Ledger` lists jobs with filters, pages through them by cursor, counts them, and answers the health counters an operator's screen opens with.

- Section: Watch and control running work, step 5
- Program: `concepts/400-observability-and-alerts/409-operator-queries` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/409-operator-queries
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. List jobs with a filter

ListJobsAsync with a namespace filter: all jobs in this namespace, three to a page, with the total.

The lines that matter:

```csharp
var page1 = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 3, IncludeTotal: true));
```

In `operator-queries.cs` (excerpt):

```csharp
// Allow the worker to process the others.
await Task.Delay(800);

Console.WriteLine("=== ListJobsAsync (namespace filter, page size 3) ===");

var page1 = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 3, IncludeTotal: true));
Console.WriteLine($"Page 1: {page1.Items.Count} items, total={page1.TotalCount}, hasMore={page1.HasMore}");
foreach (var item in page1.Items)
    Console.WriteLine($"  {item.JobRef} [{item.JobName}] status={item.Status}");

// ListJobsAsync with a status filter.
Console.WriteLine("=== ListJobsAsync (status=Cancelled filter) ===");
```

## 2. Fetch the next page by its cursor

The same query with the cursor the last page handed back.

The lines that matter:

```csharp
var page2 = await queries.Ledger.ListJobsAsync(
    new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 3, Cursor: page1.NextCursor)
);
```

In `operator-queries.cs` (excerpt):

```csharp
// ListJobsAsync keyset paging: fetch the second page via NextCursor.
Console.WriteLine("=== Keyset paging: second page via NextCursor ===");
if (page1.HasMore && page1.NextCursor is not null)
{
    var page2 = await queries.Ledger.ListJobsAsync(
        new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 3, Cursor: page1.NextCursor)
    );
    Console.WriteLine($"Page 2: {page2.Items.Count} items, hasMore={page2.HasMore}");
    foreach (var item in page2.Items)
        Console.WriteLine($"  {item.JobRef} [{item.JobName}] status={item.Status}");
}
else
{
    Console.WriteLine("(All jobs fit on one page - NextCursor was null.)");
}
```

## 3. Read the health counters

GetOverviewAsync: one-shot health counters for the whole system. Schedules, workers, and alert lists also exist on IJobs; event-timeline depth lives in rung 404.

The lines that matter:

```csharp
var overview = await operations.Ledger.GetOverviewAsync(new GetOverviewQuery());
```

In `operator-queries.cs` (excerpt):

```csharp
var withTotal = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: "operator-queries", PageSize: 1, IncludeTotal: true));
Console.WriteLine($"TotalCount across namespace: {withTotal.TotalCount}");

Console.WriteLine("=== GetOverviewAsync (system-wide) ===");

var overview = await operations.Ledger.GetOverviewAsync(new GetOverviewQuery());
Console.WriteLine($"Ready={overview.ReadyCount} Executing={overview.ExecutingCount} Failed={overview.FailedCount}");
Console.WriteLine($"UnresolvedAlerts={overview.UnresolvedAlertCount} DeadWorkers={overview.DeadWorkerCount}");

await host.StopAsync();
```

## 4. The job it acts on

`operator-queries.cs` (excerpt):

```csharp
public static class ReportJob
{
    [Job("generate-report")]
    public static void Handle(GenerateReport input) => Console.WriteLine($"[{input.ReportId}] report generated");
}
```

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/400-observability-and-alerts/409-operator-queries
```

Its output must contain:

```text
[q3] report generated
[n2] notification sent
Page 1: 3 items, total=7, hasMore=True
Cancelled jobs: 1
Page 2: 3 items, hasMore=True
TotalCount across namespace: 7
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to cancel a running job from outside](/learn/305-operator-cancel.md)

## More

- [The whole program](/learn/409-operator-queries.program.md): every file as compiled, 110 lines.
