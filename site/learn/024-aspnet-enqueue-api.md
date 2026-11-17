# How to start a job from an HTTP endpoint and poll it

A POST stores the job and answers 202 with its ref; a GET reads the status and, once the job has finished, the result.

- Section: Take the first steps, step 7
- Program: `concepts/000-fundamentals/024-aspnet-enqueue-api` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/024-aspnet-enqueue-api
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue in the POST and answer 202 with the job's ref

The job is a durable row before the response leaves, and the worker runs it after the request has ended. The ref goes into the address the client polls.

The lines that matter:

```csharp
var job = await jobs.EnqueueAsync(
    new ImportCsv(request.Tenant, request.ImportId, request.FileName),
    o => o.DeduplicationKey(deduplicationKey).CorrelationKey(correlationKey).Tag("tenant", request.Tenant).Tag("kind", "csv"),
    ct
);

var statusUrl = $"/imports/{job.JobRef}";

return Results.Accepted(statusUrl, new StartImportResponse(job.JobRef, job.Action.ToString(), statusUrl, correlationKey));
```

In `Program.cs` (excerpt):

```csharp
// POST writes a durable job and returns 202; the import runs on the worker after the request ends.
// DeduplicationKey dedupes a retry with the same tenant/import id onto the same job.
app.MapPost("/imports", async (StartImportRequest request, HttpContext http, IJobs jobs, CancellationToken ct) =>
{
    var businessKey = $"import:{request.Tenant}:{request.ImportId}";
    var deduplicationKey = DeduplicationKey.ForDefinition("import-csv", businessKey);
    var correlationKey = http.TraceIdentifier;

    var job = await jobs.EnqueueAsync(
        new ImportCsv(request.Tenant, request.ImportId, request.FileName),
        o => o.DeduplicationKey(deduplicationKey).CorrelationKey(correlationKey).Tag("tenant", request.Tenant).Tag("kind", "csv"),
        ct
    );

    var statusUrl = $"/imports/{job.JobRef}";

    return Results.Accepted(statusUrl, new StartImportResponse(job.JobRef, job.Action.ToString(), statusUrl, correlationKey));
});
```

## 2. Read the status, and the result once the job has finished

`GetAsync` answers null for a ref that names no job. A result exists only once the job has succeeded, so the read waits for a terminal status.

The lines that matter:

```csharp
var snapshot = await jobs.GetAsync(parsed, ct);
if (snapshot is null)
{
    return Results.NotFound();
}

ImportResult? result = null;
if (snapshot.Status.IsTerminal)
{
    (_, result) = await jobs.TryGetResultAsync<ImportResult>(parsed, ct);
}
```

In `Program.cs` (excerpt):

```csharp
if (!JobRef.TryParse(jobRef, out var parsed))
{
    return Results.BadRequest(new { error = "Invalid job ref." });
}

var snapshot = await jobs.GetAsync(parsed, ct);
if (snapshot is null)
{
    return Results.NotFound();
}

ImportResult? result = null;
if (snapshot.Status.IsTerminal)
{
    (_, result) = await jobs.TryGetResultAsync<ImportResult>(parsed, ct);
}
```

## 3. The job it acts on

`Program.cs` (excerpt):

```csharp
public static class ImportCsvJob
{
    [Job("import-csv")]
    public static async Task<ImportResult> Handle(ImportCsv input, CancellationToken ct)
    {
        Console.WriteLine($"[{input.Tenant}] importing {input.FileName} ({input.ImportId})");
        await Task.Delay(750, ct);
        return new ImportResult(RowsImported: 42);
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/024-aspnet-enqueue-api
```

Its output must contain:

```text
Then poll GET /imports/{jobRef}
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to build the first-job guide's app](/learn/026-first-job.md)

## More

- [The whole program](/learn/024-aspnet-enqueue-api.program.md): every file as compiled, 110 lines.
