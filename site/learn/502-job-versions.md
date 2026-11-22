# How to change a job's input without breaking the jobs already stored

A job enqueued before a deploy can run after it, so the new code has to read the input the old code stored. Keep the job's name for a change old rows survive, and give the job a new versioned name for a change they cannot.

- Section: Deploy and scale, step 6
- Program: `learn/500-payloads/502-job-versions` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/502-job-versions
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Add a field the stored rows can do without

Note is optional and has a default, so a row stored before it existed still reads: the job keeps its name.

`job-versions.cs` (excerpt):

```csharp
public sealed record SendInvoice(string InvoiceId, string? Note = null);
```

## 2. Give a change the stored rows cannot supply a new input

Every invoice now needs a currency, and the rows already stored have none. That is a new input, for a new name.

`job-versions.cs` (excerpt):

```csharp
public sealed record SendInvoiceV2(string InvoiceId, string Currency);
```

## 3. Put the new version right under the old one

Both call the same service, so the difference between them is one line each. The old handler stays registered until none of its jobs can still run, wait or be restarted; from then on there is one again.

The lines that matter:

```csharp
[Job("send-invoice")]
public Task V1(SendInvoice input, CancellationToken ct) => invoices.SendAsync(input.InvoiceId, "EUR", input.Note, ct);

[Job("send-invoice-v2")]
public Task V2(SendInvoiceV2 input, CancellationToken ct) => invoices.SendAsync(input.InvoiceId, input.Currency, null, ct);
```

In `job-versions.cs` (excerpt):

```csharp
public sealed class SendInvoiceJobs(Invoices invoices)
{
    [Job("send-invoice")]
    public Task V1(SendInvoice input, CancellationToken ct) => invoices.SendAsync(input.InvoiceId, "EUR", input.Note, ct);

    [Job("send-invoice-v2")]
    public Task V2(SendInvoiceV2 input, CancellationToken ct) => invoices.SendAsync(input.InvoiceId, input.Currency, null, ct);
}
```

## 4. Enqueue new work under the new name

The callers move to the new input. The rows already stored under the old name keep their handler.

The lines that matter:

```csharp
var next = await jobs.EnqueueAsync(new SendInvoiceV2("INV-3", "USD"));
```

In `job-versions.cs` (excerpt):

```csharp
var next = await jobs.EnqueueAsync(new SendInvoiceV2("INV-3", "USD"));

(await jobs.WaitAsync(stored)).ThrowIfFailed();
(await current.WaitAsync()).ThrowIfFailed();
(await next.WaitAsync()).ThrowIfFailed();

await host.StopAsync();
```

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/500-payloads/502-job-versions
```

Its output must contain:

```text
sent INV-1 in EUR
sent INV-2 in EUR (note: thanks)
sent INV-3 in USD
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A job enqueued before a deploy can run after it. `SendInvoice` is stored with each job, so the new code has to read what the old code wrote.
- Keep the name `send-invoice` for a change the stored rows survive: an optional, nullable or defaulted field, such as `Note`.
- Use a new versioned name, `send-invoice-v2`, when the stored rows would be misread or rejected: a renamed, removed or retyped field, a changed meaning, or required data they cannot supply, such as `Currency`.
- Put the new version right under the old one. From then on there are two to maintain, until the old one's jobs have finished.
- Keep the old handler registered until none of its jobs can still run, wait or be restarted. Dropping it cancels nothing: its jobs stay Ready with no handler to run them.
- The name in `[Job("send-invoice")]` and the namespace in `Run<JobVersionsJobs>("job-versions")` are stored with every job too. A version is a new name beside the old one, never a rename.
- Inside a handler, a step, signal, timer, variable and child name is a key into what the job has stored. A renamed step runs again.

## Check yourself

1. A `send-invoice` job is waiting for its next retry when the build with `send-invoice-v2` is deployed. Which handler runs it?
   - a) The `send-invoice-v2` handler, since it is the newest
   - b) The `send-invoice` handler, since the job's name is stored with the job
   - c) Neither, until an operator moves the job to the new name
   - Answer: b. That is why the old handler stays registered: the stored jobs still ask for it by name.
2. A stored row has no `Note`. What does the handler read?
   - a) The job fails, since a field is missing
   - b) `null`, the field's default
   - c) An empty string
   - Answer: b. An optional field with a default is a change the stored rows survive, so the job keeps its name.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to give Acta a JSON context for Native AOT](/learn/901-native-aot-json.md)

## More

- [The whole program](/learn/502-job-versions.program.md): every file as compiled, 84 lines.
