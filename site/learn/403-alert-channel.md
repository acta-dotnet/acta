# How to send alerts to your own channel

`AddAlertChannel` declares where alerts go, under a name of your choice, and an alert names the channel it is for.

- Section: Get told when something breaks, step 3
- Program: `learn/400-observability-and-alerts/403-alert-channel` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/403-alert-channel
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Declare the channel on the worker

Alert channels declare process-local delivery configuration. Alert rows persist only the selected channel name; endpoints and secrets stay in app configuration. MinSeverity = Warning suppresses lower-severity alerts at delivery.

The lines that matter:

```csharp
w.AddAlertChannel(
    "ops",
    AlertTransportKinds.Log,
    endpoint: "ops", // the log transport ignores the endpoint; a Slack channel passes its URL here
    o =>
    {
        o.MinSeverity = AlertSeverityCode.Warning;
    }
);
```

In `alert-channel.cs` (excerpt):

```csharp
w.AddManifest<AlertChannelJobs>();

w.AddAlertChannel(
    "ops",
    AlertTransportKinds.Log,
    endpoint: "ops", // the log transport ignores the endpoint; a Slack channel passes its URL here
    o =>
    {
        o.MinSeverity = AlertSeverityCode.Warning;
    }
);
```

## 2. Name the channel when you raise the alert

Route to the "ops" channel instead of the default.

The lines that matter:

```csharp
await context.AlertAsync(
    title: $"Low disk space on {input.Server}",
    message: $"Only {input.FreePercent}% free on {input.Server}.",
    severityCode: AlertSeverityCode.Error,
    channelName: "ops",
    deduplicationKey: $"disk:{input.Server}",
    ct: ct
);
```

In `alert-channel.cs` (excerpt):

```csharp
await context.AlertAsync(
    title: $"Low disk space on {input.Server}",
    message: $"Only {input.FreePercent}% free on {input.Server}.",
    severityCode: AlertSeverityCode.Error,
    channelName: "ops",
    deduplicationKey: $"disk:{input.Server}",
    ct: ct
);
Console.WriteLine($"[{input.Server}] low disk - alerted ops ({input.FreePercent}% free)");
return;
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/403-alert-channel
```

Its output must contain:

```text
[db-server] low disk - alerted ops (4% free)
alert [Error] Low disk space on db-server -> channel 'ops'
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to route alerts to Slack from configuration](/learn/405-real-alert-routing.md)

## More

- [The whole program](/learn/403-alert-channel.program.md): every file as compiled, 85 lines.
