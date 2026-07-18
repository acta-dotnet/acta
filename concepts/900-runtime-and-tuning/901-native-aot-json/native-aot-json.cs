// Concept: AOT-safe JSON payloads via j.UseJsonPayloads(JsonSerializerContext) and
// JobPayload.Json(value, typeInfo) -- no runtime reflection touches the payload type.
// The NativeAOT *publish* guarantee is enforced by the framework's CI guardrail
// (tests/Acta.Tests/Aot/NativeAotPublishTests.cs, gated by ACTA_AOT_PUBLISH_TEST), which publishes
// anvil/Anvil with NativeAOT. This rung teaches the consumer-side API, not the publish step.
using System.Text.Json.Serialization;
using Acta;
using Acta.Concepts.NativeAotJson;
using Acta.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    // Wire the source-generated resolver so payload (de)serialization needs no reflection.
    j.UseJsonPayloads(AotPayloadContext.Default);
    j.Run<NativeAotJsonJobs>("native-aot-json");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("Enqueuing with source-generated type info (no reflection)...");

// JobPayload.Json(value, typeInfo) is the AOT-safe path: the JsonTypeInfo comes from the
// source-generated context so no runtime reflection touches the payload type.
var payload = JobPayload.Json(new ProcessOrder("ORD-001", 3), AotPayloadContext.Default.ProcessOrder);
var request = JobRequestBuilder.Create("native-aot-json", "process-order").Payload(payload).Build();

var outcome = await jobs.EnqueueAsync(request, CancellationToken.None);
Console.WriteLine($"Enqueued {outcome.JobRef} ({outcome.Action})");

await Task.Delay(600);

var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"Status: {snapshot!.Status}");

await host.StopAsync();

namespace Acta.Concepts.NativeAotJson
{
    public sealed record ProcessOrder(string OrderId, int Quantity);

    // Source-generated context: covers every job input/output type so no runtime reflection
    // is needed under Native AOT. Wired via j.UseJsonPayloads(AotPayloadContext.Default).
    [JsonSerializable(typeof(ProcessOrder))]
    internal sealed partial class AotPayloadContext : JsonSerializerContext;

    public static class ProcessOrderJob
    {
        [Job("process-order")]
        public static void Handle(ProcessOrder input) => Console.WriteLine($"Processed order {input.OrderId} x{input.Quantity}");
    }
}
