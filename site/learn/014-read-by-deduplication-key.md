# How to find a job by its deduplication key

`JobLookup.ByDeduplicationKey` finds a job by the key it was enqueued with, when you did not keep its ref.

- Section: Start jobs, step 12
- Program: `concepts/000-fundamentals/014-read-by-deduplication-key` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/014-read-by-deduplication-key
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue with a key you can build again later

Typed options accept an exact final key; compose the definition-qualified key explicitly.

The lines that matter:

```csharp
var businessKey = "welcome:sam";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));
```

In `read-by-deduplication-key.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var businessKey = "welcome:sam";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));

// Lookup uses the exact stored final key; it does not compose a definition name from a business key.
var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey("read-by-deduplication-key", finalKey));

Console.WriteLine($"Found job {snapshot!.JobRef} by deduplication key '{snapshot.DeduplicationKey}', status {snapshot.Status}.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");
```

## 2. Look the job up by that key

Lookup uses the exact stored final key; it does not compose a definition name from a business key.

The lines that matter:

```csharp
var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey("read-by-deduplication-key", finalKey));
```

In `read-by-deduplication-key.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

// Typed options accept an exact final key; compose the definition-qualified key explicitly.
var businessKey = "welcome:sam";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);
await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));

var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey("read-by-deduplication-key", finalKey));

Console.WriteLine($"Found job {snapshot!.JobRef} by deduplication key '{snapshot.DeduplicationKey}', status {snapshot.Status}.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");
```

## 3. The job it acts on

`read-by-deduplication-key.cs` (excerpt):

```csharp
public sealed record SendWelcomeEmail(string Email, string Name);

public static class WelcomeJob
{
    [Job("send-welcome-email")]
    public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/014-read-by-deduplication-key
```

Its output must contain:

```text
by deduplication key 'send-welcome-email:welcome:sam'
Sent welcome email to Sam <sam@example.com>
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to choose between starting a job and waiting for it](/learn/015-call-vs-enqueue.md)

## More

- [The whole program](/learn/014-read-by-deduplication-key.program.md): every file as compiled, 49 lines.
