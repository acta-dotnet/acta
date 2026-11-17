# How to route alerts to Slack from configuration

An alert channel posts to a Slack webhook whose URL comes from configuration, and logs when none is set. A deduplication key collapses repeats of one incident into one alert.

- Section: Get told when something breaks, step 4
- Program: `concepts/400-observability-and-alerts/405-real-alert-routing` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/405-real-alert-routing
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Declare a Slack channel with its URL from configuration

Built-in Slack incoming-webhook transport, with the webhook URL from configuration (Alerts:SlackWebhookUrl). Without one the channel logs instead, so a plain run sends nothing. Acta SQL stores only channel_name = "ops-slack" either way. MinSeverity = Warning suppresses lower-severity alerts at delivery.

The lines that matter:

```csharp
var webhookUrl = builder.Configuration["Alerts:SlackWebhookUrl"];
w.AddAlertChannel(
    "ops-slack",
    webhookUrl is null ? AlertTransportKinds.Log : AlertTransportKinds.SlackWebhook,
    endpoint: webhookUrl ?? "ops-slack",
    o =>
    {
        o.MinSeverity = AlertSeverityCode.Warning;
    }
);
```

In `real-alert-routing.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.Run("real-alert-routing", w =>
    {
        w.AddManifest<RealAlertRoutingJobs>();

        var webhookUrl = builder.Configuration["Alerts:SlackWebhookUrl"];
        w.AddAlertChannel(
            "ops-slack",
            webhookUrl is null ? AlertTransportKinds.Log : AlertTransportKinds.SlackWebhook,
            endpoint: webhookUrl ?? "ops-slack",
            o =>
            {
                o.MinSeverity = AlertSeverityCode.Warning;
            }
        );
    });
});

using var host = builder.Build();
await host.StartAsync();
```

## 2. Raise the alerts of one incident under one key

deduplicationKey ties repeats together onto one open incident row.

The lines that matter:

```csharp
await context.AlertAsync(
    title: $"Queue backlog: {input.Queue}",
    message: $"'{input.Queue}' depth is {input.Depth}, above the 10k threshold.",
    severityCode: AlertSeverityCode.Error,
    channelName: "ops-slack",
    deduplicationKey: $"queue-depth:{input.Queue}",
    ct: ct
);
```

In `real-alert-routing.cs` (excerpt):

```csharp
public sealed record QueueDepthCheck(string Queue, int Depth);

public sealed class QueueDepthCheckJob
{
    [Job("queue-depth-check")]
    public async Task Handle(QueueDepthCheck input, JobContext context, CancellationToken ct)
    {
        await Task.Delay(50, ct);

        if (input.Depth > 10_000)
        {
            await context.AlertAsync(
                title: $"Queue backlog: {input.Queue}",
                message: $"'{input.Queue}' depth is {input.Depth}, above the 10k threshold.",
                severityCode: AlertSeverityCode.Error,
                channelName: "ops-slack",
                deduplicationKey: $"queue-depth:{input.Queue}",
                ct: ct
            );
            Console.WriteLine($"[{input.Queue}] depth {input.Depth} - alerted ops-slack");
            return;
        }

        Console.WriteLine($"[{input.Queue}] depth {input.Depth} OK");
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/400-observability-and-alerts/405-real-alert-routing
```

Its output must contain:

```text
alert [Error] Queue backlog: orders -> 'ops-slack' x2
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to escalate an alert as failures add up](/learn/411-alert-escalation.md)

## More

- [The whole program](/learn/405-real-alert-routing.program.md): every file as compiled, 90 lines.
