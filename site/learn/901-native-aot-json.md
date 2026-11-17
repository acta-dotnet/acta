# How to run jobs under Native AOT

A source-generated `JsonSerializerContext` given to `UseJsonPayloads` serializes payloads with no runtime reflection, and `JobPayload.Json(value, typeInfo)` builds a raw payload the same way.

- Section: Deploy and scale, step 5
- Program: `concepts/900-runtime-and-tuning/901-native-aot-json` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/901-native-aot-json
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Declare a source-generated context for the payload types

Source-generated context: covers every job input and result type so no runtime reflection is needed under Native AOT. Wired via j.UseJsonPayloads(AotPayloadContext.Default). The two options give enums the wire shape a JIT host writes (camelCase strings), so an AOT worker reads what a JIT producer enqueued.

The lines that matter:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProcessOrder))]
internal sealed partial class AotPayloadContext : JsonSerializerContext;
```

In `native-aot-json.cs` (excerpt):

```csharp
public sealed record ProcessOrder(string OrderId, int Quantity);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProcessOrder))]
internal sealed partial class AotPayloadContext : JsonSerializerContext;

public static class ProcessOrderJob
{
    [Job("process-order")]
    public static void Handle(ProcessOrder input) => Console.WriteLine($"Processed order {input.OrderId} x{input.Quantity}");
}
```

## 2. Hand the context to Acta

Wire the source-generated resolver so payload (de)serialization needs no reflection.

The lines that matter:

```csharp
j.UseJsonPayloads(AotPayloadContext.Default);
```

In `native-aot-json.cs` (excerpt):

```csharp
// The NativeAOT publish itself is proven by the repository's own gate
// (tests/Acta.Tests/Architecture/NativeAotPublishTests.cs, gated by ACTA_AOT_PUBLISH_TEST), which publishes
// anvil/Anvil with NativeAOT. This program shows the consumer-side API, not the publish step.
using System.Text.Json.Serialization;
using Acta;
using Acta.Concepts.NativeAotJson;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.UseJsonPayloads(AotPayloadContext.Default);
    j.Run<NativeAotJsonJobs>("native-aot-json");
});

using var host = builder.Build();
await host.StartAsync();
```

## 3. Build a raw payload with its type info

JobPayload.Json(value, typeInfo) is the AOT-safe path: the JsonTypeInfo comes from the source-generated context so no runtime reflection touches the payload type.

The lines that matter:

```csharp
var payload = JobPayload.Json(new ProcessOrder("ORD-001", 3), AotPayloadContext.Default.ProcessOrder);
```

In `native-aot-json.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("Enqueuing with source-generated type info (no reflection)...");

var payload = JobPayload.Json(new ProcessOrder("ORD-001", 3), AotPayloadContext.Default.ProcessOrder);

var request = JobRequestBuilder.Create("native-aot-json", "process-order").Payload(payload).Build();

var enqueued = await jobs.EnqueueAsync(request, CancellationToken.None);
Console.WriteLine($"Enqueued {enqueued.JobRef} ({enqueued.Action})");

await jobs.WaitAsync(enqueued);

var snapshot = await jobs.GetAsync(enqueued);
Console.WriteLine($"Status: {snapshot!.Status}");
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/900-runtime-and-tuning/901-native-aot-json
```

Its output must contain:

```text
Processed order ORD-001 x3
Status: Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to wake workers across processes with Redis](/learn/903-redis-wakeup.md)

## More

- [The whole program](/learn/901-native-aot-json.program.md): every file as compiled, 71 lines.
