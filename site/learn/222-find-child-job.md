# How to find the child job that handles one item

A child's name on the reads is its deduplication key, `{map}/{key}` for a child that a map started. List the parent's children and pick the one that carries the item's key.

- Section: Split work across child jobs, step 5
- Program: `learn/200-durable-execution/222-find-child-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/222-find-child-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Start each child under the item's own key

`itemKey` names each child after its item. The name is stored as the child's deduplication key, and it is how a replay of the parent and a reader from outside find the same child.

The lines that matter:

```csharp
var sent = await ctx.MapAsync("invoice", batch.InvoiceIds, itemKey: id => id, child: id => new SendInvoice(id), ct);
```

In `find-child-job.cs` (excerpt):

```csharp
public sealed class InvoiceJobs
{
    [Job("send-invoices")]
    public async Task Handle(SendInvoices batch, JobContext ctx, CancellationToken ct)
    {
        var sent = await ctx.MapAsync("invoice", batch.InvoiceIds, itemKey: id => id, child: id => new SendInvoice(id), ct);
        Console.WriteLine($"{sent.Items.Count(item => item.Outcome.Succeeded)} of {batch.InvoiceIds.Length} invoices sent");
    }

    [Job("send-invoice")]
    public async Task Send(SendInvoice invoice, JobContext ctx, CancellationToken ct)
    {
        // FailAsync ends the job there, Failed with no retry, so only the other invoices print as sent.
        if (invoice.InvoiceId == 7)
        {
            await ctx.FailAsync("the customer's address bounced", ct);
        }
        Console.WriteLine($"invoice {invoice.InvoiceId} sent");
    }
}
```

## 2. List the parent's children

`Parent` restricts the job list to the children of one job. Each child shows the name it was started under.

The lines that matter:

```csharp
var children = await operations.Ledger.ListJobsAsync(new ListJobsQuery(Parent: parent));
foreach (var child in children.Items.OrderBy(child => child.DeduplicationKey, StringComparer.Ordinal))
{
    Console.WriteLine($"{child.DeduplicationKey}: {child.Status.Code}");
}
```

In `find-child-job.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();
var operations = host.Services.GetRequiredService<IActaOperations>();

var parent = await jobs.EnqueueAsync(new SendInvoices([5, 6, 7]));
await parent.WaitAsync();

var children = await operations.Ledger.ListJobsAsync(new ListJobsQuery(Parent: parent));
foreach (var child in children.Items.OrderBy(child => child.DeduplicationKey, StringComparer.Ordinal))
{
    Console.WriteLine($"{child.DeduplicationKey}: {child.Status.Code}");
}

var invoice7 = children.Items.Single(child => child.DeduplicationKey == "invoice/7");
var why = (await jobs.ExplainAsync(invoice7.JobRef))!;
Console.WriteLine($"invoice 7: {why.Headline} {why.Reason}");

await host.StopAsync();
```

## 3. Pick the child that carries the item's key

The key is the one the map was given for that item, so a page that shows invoice 7 gets from the invoice to its job, and from there to what the job did.

The lines that matter:

```csharp
var invoice7 = children.Items.Single(child => child.DeduplicationKey == "invoice/7");
var why = (await jobs.ExplainAsync(invoice7.JobRef))!;
Console.WriteLine($"invoice 7: {why.Headline} {why.Reason}");
```

In `find-child-job.cs` (excerpt):

```csharp
var children = await operations.Ledger.ListJobsAsync(new ListJobsQuery(Parent: parent));
foreach (var child in children.Items.OrderBy(child => child.DeduplicationKey, StringComparer.Ordinal))
{
    Console.WriteLine($"{child.DeduplicationKey}: {child.Status.Code}");
}

var invoice7 = children.Items.Single(child => child.DeduplicationKey == "invoice/7");
var why = (await jobs.ExplainAsync(invoice7.JobRef))!;
Console.WriteLine($"invoice 7: {why.Headline} {why.Reason}");

await host.StopAsync();
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/222-find-child-job
```

Its output must contain:

```text
2 of 3 invoices sent
invoice/5: succeeded
invoice/7: failed
invoice 7: Failed. the customer's address bounced
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A child's name on the job reads is its `DeduplicationKey`: the call's name for `EnqueueChildAsync`, `{map}/{key}` for a map's child.
- Keys keep their case: `Invoice/7` and `invoice/7` are two keys, so build the key the same way where you start the child and where you look for it.
- The list answers a page at a time, fifty jobs unless `PageSize` says otherwise and a hundred at most. A parent with more children is read page by page, as in [list and page through jobs](/learn/409-operator-queries.md).
- A failed child does not fail its parent by itself, so a parent that reads `succeeded` can still have a child that failed. [Handle child jobs that fail](/learn/219-child-failure-outcomes.md) shows the parent's side.
- `"invoice"` is how `send-invoices` finds the child for each item. A renamed child is new work: every invoice is sent again.
- A parent and its children are each stored under their own job name. The names in `[Job("send-invoices")]` and `[Job("send-invoice")]`, and the namespace in `Run<FindChildJobJobs>("find-child-job")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendInvoices` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-invoices-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Check yourself

1. A parent mapped invoices 5, 6 and 7 under the name `invoice`. Which deduplication key does the child for invoice 6 carry?
   - a) `6`
   - b) `invoice/6`
   - c) `send-invoice/6`
   - Answer: b. The name the map was given comes first, then the item's key. The job's own name, `send-invoice`, is not part of it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to handle child jobs that fail](/learn/219-child-failure-outcomes.md)

## More

- [The whole program](/learn/222-find-child-job.program.md): every file as compiled, 81 lines.
