using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Acta.Verify;

/// <summary>
/// A written scenario: what it asks, the runner that executes it, its workload, topology, load and target. A value that
/// differs by database is held per database, and a database the scenario writes nothing for does not run it.
/// </summary>
public sealed record Scenario(
    string Id,
    string Group,
    string Title,
    string Question,
    string Runner,
    string? Headline,
    IReadOnlyList<WorkShape> Work,
    IReadOnlyDictionary<string, int> Workers,
    int? Executors,
    IReadOnlyDictionary<string, int> Namespaces,
    IReadOnlyDictionary<string, int> Calls,
    int JobsPerCall,
    IReadOnlyDictionary<string, int[]> Rates,
    IReadOnlyDictionary<string, int> Jobs,
    int? Seconds,
    bool AdmissionOnly,
    int Lanes,
    string? Lane,
    string? ConcurrencyKey,
    double? P99Ms,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyDictionary<string, string> Skip
)
{
    /// <summary>The input of arrival <paramref name="index"/>: the written work shapes, taken in turn.</summary>
    public WorkInput WorkAt(int index, int seed) => Work[index % Work.Count].ToInput(LoadTrial.Key(index), index, seed);

    /// <summary>Whether the workload calls the external effect service: an effect, or a barrier it holds a body at.</summary>
    [JsonIgnore]
    public bool NeedsEffectService => Work.Any(w => w.EffectMode is not null || w.Boundary is not null);

    /// <summary>Whether the workload starts children in another namespace than its own.</summary>
    [JsonIgnore]
    public bool CrossNamespace => Work.Any(w => w.ChildNamespace != "verify");

    /// <summary>The lane arrival <paramref name="index"/> joins, when the scenario writes one.</summary>
    public string? LaneAt(int index) => Lanes > 0 ? $"lane-{index % Lanes}" : Lane;

    /// <summary>The number the runner setting <paramref name="name"/> holds for <paramref name="provider"/>.</summary>
    public double Setting(string name, string provider) => For(name, provider).GetDouble();

    /// <summary>The number the runner setting <paramref name="name"/> holds for <paramref name="provider"/>, or null when it writes none.</summary>
    public double? SettingOrNull(string name, string provider) =>
        Settings.ContainsKey(name) && For(name, provider) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    /// <summary>The text the runner setting <paramref name="name"/> holds for <paramref name="provider"/>, or null when it writes none.</summary>
    public string? SettingText(string name, string provider) => Settings.ContainsKey(name) ? For(name, provider).GetString() : null;

    /// <summary>The numbers the runner setting <paramref name="name"/> holds for <paramref name="provider"/>.</summary>
    public int[] SettingList(string name, string provider) => [.. For(name, provider).EnumerateArray().Select(e => e.GetInt32())];

    private JsonElement For(string name, string provider) =>
        Settings.TryGetValue(name, out var value)
            ? value.ValueKind == JsonValueKind.Object
                ? value.GetProperty(provider)
                : value
            : throw new InvalidOperationException($"Scenario {Id} writes no setting {name}.");
}

/// <summary>One job shape a scenario's arrivals take in turn; a payload is written as its size and generated from the arrival.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkShape(
    int Steps = 0,
    int Children = 0,
    int Depth = 0,
    int WorkMs = 0,
    int Failures = 0,
    bool Poison = false,
    string? Signal = null,
    int SleepSeconds = 0,
    int? PayloadBytes = null,
    int CpuRounds = 0,
    string ChildNamespace = "verify",
    int ChildWindow = 100,
    bool PartialFailure = false,
    string? Boundary = null,
    string? EffectMode = null,
    bool AtMostOnce = false,
    bool Witness = false,
    int SignalTimeoutSeconds = 0,
    string? AfterChildrenSignal = null
)
{
    public WorkInput ToInput(string key, int index, int seed) =>
        new(
            key,
            Steps,
            Children,
            Depth,
            WorkMs,
            Failures,
            Poison,
            Signal,
            SleepSeconds,
            PayloadBytes is { } bytes ? WorkloadModel.Payload(bytes, unchecked(seed + index)) : null,
            CpuRounds,
            ChildNamespace,
            ChildWindow,
            PartialFailure,
            Boundary,
            EffectMode,
            AtMostOnce,
            Witness,
            SignalTimeoutSeconds,
            AfterChildrenSignal
        );
}

/// <summary>
/// A suite: the scenarios a run executes, in order, with the timings and passes it applies to them, whether measurement
/// decides its verdict, and the fields it replaces in named scenarios.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Suite(
    string Title,
    string Purpose,
    IReadOnlyList<string> Scenarios,
    int WarmupSeconds,
    int LadderSeconds,
    int SingleSeconds,
    int Passes,
    int HeadlinePasses,
    int ConfirmSeconds,
    bool GatesPerformance,
    bool FirstRateOnly,
    int FaultRepetitions,
    IReadOnlyDictionary<string, JsonObject>? Overrides = null
);

/// <summary>
/// Reads the scenario and suite files embedded in this build, strictly: a field the scenario's runner does not read is
/// an error, and a field written as null is unwritten.
/// </summary>
public static class ScenarioFiles
{
    /// <summary>The suite named <paramref name="name"/>.</summary>
    public static Suite Suite(string name) =>
        JsonSerializer.Deserialize(Protocol.Resource($"Acta.Verify.Suites.{name}.json"), VerifyJson.Default.Suite)
        ?? throw new InvalidOperationException($"Empty suite {name}.");

    /// <summary>The suites this build carries.</summary>
    public static string[] SuiteNames() =>
        [.. Protocol.Resources("Acta.Verify.Suites.").Select(r => r["Acta.Verify.Suites.".Length..^".json".Length])];

    /// <summary>Every scenario this build carries, with <paramref name="suite"/>'s overrides applied.</summary>
    public static Scenario[] All(Suite? suite)
    {
        List<Scenario> scenarios = [];
        foreach (var resource in Protocol.Resources("Acta.Verify.Scenarios."))
        {
            // The resource keeps the file's folder, with either separator: Acta.Verify.Scenarios.<group>/<name>.json.
            var path = resource["Acta.Verify.Scenarios.".Length..^".json".Length].Split('\\', '/');
            var (group, name) = (path[0], path[^1]);
            var node =
                JsonNode.Parse(Protocol.Resource(resource))?.AsObject() ?? throw new InvalidOperationException($"Empty scenario {name}.");
            if (suite?.Overrides?.GetValueOrDefault(name) is { } overrides)
            {
                foreach (var (field, value) in overrides)
                {
                    var (target, key) = field.StartsWith("settings.", StringComparison.Ordinal)
                        ? ((node["settings"] ??= new JsonObject()).AsObject(), field["settings.".Length..])
                        : (node, field);
                    target[key] = value?.DeepClone();
                }
            }
            scenarios.Add(Parse(group, name, JsonSerializer.SerializeToElement(node, VerifyJson.Default.JsonObject)));
        }
        if (suite is not null && suite.Overrides?.Keys.FirstOrDefault(k => !scenarios.Any(s => s.Id == k)) is { } stray)
        {
            throw new InvalidOperationException($"The suite overrides {stray}, which is not a scenario.");
        }
        return [.. scenarios];
    }

    private static Scenario Parse(string group, string name, JsonElement file)
    {
        bool Has(string field, out JsonElement value) => file.TryGetProperty(field, out value) && value.ValueKind != JsonValueKind.Null;
        string? Text(string field) => Has(field, out var value) ? value.GetString() : null;
        string Required(string field) => Text(field) ?? throw new InvalidOperationException($"Scenario {group}/{name} needs {field}.");
        int? Number(string field) => Has(field, out var value) ? value.GetInt32() : null;
        var runner = Required("runner");
        if (!ScenarioRunner.Names.Contains(runner))
        {
            throw new InvalidOperationException($"Scenario {group}/{name} names the runner {runner}, which does not exist.");
        }
        if (file.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !ScenarioRunner.Reads(runner, n)) is { } unread)
        {
            throw new InvalidOperationException($"Scenario {group}/{name} writes {unread}, which the {runner} runner does not read.");
        }
        Dictionary<string, T> PerDatabase<T>(string field, Func<JsonElement, T> read, Func<T>? fallback = null)
        {
            if (!Has(field, out var value))
            {
                return fallback is null ? [] : Protocol.AllProviders.ToDictionary(p => p, _ => fallback(), StringComparer.Ordinal);
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                return Protocol.AllProviders.ToDictionary(p => p, _ => read(value), StringComparer.Ordinal);
            }
            if (value.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !Protocol.AllProviders.Contains(n)) is { } stray)
            {
                throw new InvalidOperationException($"Scenario {group}/{name} writes {field} for {stray}, which is not a database.");
            }
            return value.EnumerateObject().ToDictionary(p => p.Name, p => read(p.Value), StringComparer.Ordinal);
        }
        var work = Has("work", out var shapes) ? JsonSerializer.Deserialize(shapes, VerifyJson.Default.WorkShapeArray) ?? [] : [];
        return new(
            name,
            group,
            Required("title"),
            Required("question"),
            runner,
            Text("headline"),
            work.Length == 0 ? [new WorkShape()] : work,
            PerDatabase("workers", v => v.GetInt32(), () => 1),
            Number("executors"),
            PerDatabase("namespaces", v => v.GetInt32(), () => 1),
            PerDatabase("calls", v => v.GetInt32()),
            Number("jobs_per_call") ?? 1,
            PerDatabase("rates", v => v.EnumerateArray().Select(r => r.GetInt32()).ToArray()),
            PerDatabase("jobs", v => v.GetInt32()),
            Number("seconds"),
            Has("admission_only", out var admission) && admission.GetBoolean(),
            Number("lanes") ?? 0,
            Text("lane"),
            Text("concurrency_key"),
            Has("p99_ms", out var p99) ? p99.GetDouble() : null,
            Has("settings", out var settings)
                ? settings
                    .EnumerateObject()
                    .Where(p => p.Value.ValueKind != JsonValueKind.Null)
                    .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : [],
            PerDatabase("skip", v => v.GetString()!)
        );
    }
}
