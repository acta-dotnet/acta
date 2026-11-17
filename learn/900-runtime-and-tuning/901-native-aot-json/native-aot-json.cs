// How to run jobs under Native AOT
// A source-generated `JsonSerializerContext` given to `UseJsonPayloads` serializes payloads with no runtime
// reflection, and `JobPayload.Json(value, typeInfo)` builds a raw payload the same way. The program runs on the JIT:
// it shows the registration an AOT publish needs, and the publish itself is proven by the repository's own gate.

// The NativeAOT publish itself is proven by the repository's own gate
// (tests/Acta.Tests/Architecture/NativeAotPublishTests.cs, gated by ACTA_AOT_PUBLISH_TEST), which publishes
// anvil/Anvil with NativeAOT. This program shows the consumer-side API, not the publish step.
using System.Text.Json.Serialization;
using Acta;
using Acta.Learn.NativeAotJson;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    // learn: 2. Hand the context to Acta
    // Wire the source-generated resolver so payload (de)serialization needs no reflection.
    j.UseJsonPayloads(AotPayloadContext.Default);
    // learn: end
    j.Run<NativeAotJsonJobs>("native-aot-json");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("Enqueuing with source-generated type info (no reflection)...");

// learn: 3. Build a raw payload with its type info
// JobPayload.Json(value, typeInfo) is the AOT-safe path: the JsonTypeInfo comes from the
// source-generated context so no runtime reflection touches the payload type.
var payload = JobPayload.Json(new ProcessOrder("ORD-001", 3), AotPayloadContext.Default.ProcessOrder);

// learn: end
var request = JobRequestBuilder.Create("native-aot-json", "process-order").Payload(payload).Build();

var enqueued = await jobs.EnqueueAsync(request, CancellationToken.None);
Console.WriteLine($"Enqueued {enqueued.JobRef} ({enqueued.Action})");

await jobs.WaitAsync(enqueued);

var snapshot = await jobs.GetAsync(enqueued);
Console.WriteLine($"Status: {snapshot!.Status}");

await host.StopAsync();

namespace Acta.Learn.NativeAotJson
{
    public sealed record ProcessOrder(string OrderId, int Quantity);

    // learn: 1. Declare a source-generated context for the payload types
    // Source-generated context: covers every job input and result type so no runtime reflection
    // is needed under Native AOT. Wired via j.UseJsonPayloads(AotPayloadContext.Default). The two
    // options give enums the wire shape a JIT host writes (camelCase strings), so an AOT worker reads
    // what a JIT producer enqueued.
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(ProcessOrder))]
    internal sealed partial class AotPayloadContext : JsonSerializerContext;

    // learn: end

    public static class ProcessOrderJob
    {
        [Job("process-order")]
        public static void Handle(ProcessOrder input) => Console.WriteLine($"Processed order {input.OrderId} x{input.Quantity}");
    }
}
