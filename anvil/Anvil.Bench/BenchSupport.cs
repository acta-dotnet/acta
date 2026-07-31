using System.Text.Json.Serialization;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Bench run identity: a fresh per-run schema so each measurement starts on an empty, isolated schema.
/// Mirrors the lab's run-id shape (fixed-width timestamp + random suffix) so two runs started in the
/// same wall-clock second cannot collide; schema names are valid SQL identifiers (underscores).
/// </summary>
public static class BenchIdentity
{
    public static string NewSchema(DateTime utcNow, string? suffix = null)
    {
        suffix ??= Guid.NewGuid().ToString("N")[..6];
        var runId = $"r{utcNow:yyyyMMdd-HHmmss}-{suffix}";
        return $"anvil_bench_{runId.Replace('-', '_')}";
    }
}

/// <summary>Reflection-free payload builder for the bench workload input (AOT-clean enqueue).</summary>
internal static class BenchPayloads
{
    public static JobPayload Json(BenchInput v) => JobPayload.Json(v, BenchPayloadJsonContext.Default.BenchInput);
}

/// <summary>
/// Source-generated payload context for the bench workload types, wired via
/// <c>j.UseJsonPayloads(BenchPayloadJsonContext.Default)</c> so payload (de)serialization needs no
/// reflection under Native AOT. The wire-shape options mirror the framework defaults.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
[JsonSerializable(typeof(BenchInput))]
[JsonSerializable(typeof(BenchResultPayload))]
internal sealed partial class BenchPayloadJsonContext : JsonSerializerContext;
