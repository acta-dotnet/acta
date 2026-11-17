# How to raise an alert from a handler

`AlertAsync` in a handler raises an alert for the people who operate the system, and `operations.Alerts` reads it back.

- Section: Get told when something breaks, step 2
- Program: `learn/400-observability-and-alerts/402-alerts` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/402-alerts
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Raise the alert from the handler

No channel name routes to the implicit "default" channel (log transport). The database stores only the channel name; delivery configuration comes from worker startup.

The lines that matter:

```csharp
await context.AlertAsync(
    title: $"Backup verification failed: {input.FolderName}",
    message: $"The nightly backup of '{input.FolderName}' is missing or corrupt.",
    severityCode: AlertSeverityCode.Error,
    deduplicationKey: $"backup-failed:{input.FolderName}",
    ct: ct
);
```

In `alerts.cs` (excerpt):

```csharp
[Job("verify-backup")]
public async Task Handle(VerifyBackup input, JobContext context, CancellationToken ct)
{
    await Task.Delay(50, ct);

    if (!input.Healthy)
    {
        await context.AlertAsync(
            title: $"Backup verification failed: {input.FolderName}",
            message: $"The nightly backup of '{input.FolderName}' is missing or corrupt.",
            severityCode: AlertSeverityCode.Error,
            deduplicationKey: $"backup-failed:{input.FolderName}",
            ct: ct
        );
        Console.WriteLine($"[{input.FolderName}] backup is bad - alert raised");
        return;
    }

    Console.WriteLine($"[{input.FolderName}] backup verified OK");
}
```

## 2. Read the alerts back

An alert is a row: the list answers its severity, its channel, and whether it was delivered.

The lines that matter:

```csharp
var alerts = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: "alerts"));
```

In `alerts.cs` (excerpt):

```csharp
// Handler raises an operator alert; read it back through the read-only operator query surface.
await jobs.EnqueueAsync(new VerifyBackup("photos", Healthy: false));
await Task.Delay(800);

var queries = host.Services.GetRequiredService<IActaOperations>();

var alerts = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: "alerts"));
foreach (var alert in alerts.Items)
{
    Console.WriteLine($"alert [{alert.Severity}] {alert.Title} -> channel '{alert.ChannelName}', delivery {alert.DeliveryStatus}");
}

await host.StopAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/402-alerts
```

Its output must contain:

```text
[photos] backup is bad - alert raised
alert [Error] Backup verification failed: photos -> channel 'default'
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The `AlertAsync` call is a bare line, so it runs again on every re-entry of the handler: without a `deduplicationKey` each run inserts another alert. Raise it in a step, or give it a key.
- `AlertAsync` answers its `AlertRef`, and `acta.Alerts.ResolveAsync(alertRef)` resolves it. An alert raised with a key resolves with `ctx.ResolveAlertAsync(key, ct)`.
- The row is written at once; delivery to the channel runs on the `sys.alerts` pass, within about a minute.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to send alerts to your own channel](/learn/403-alert-channel.md)

## More

- [The whole program](/learn/402-alerts.program.md): every file as compiled, 71 lines.
