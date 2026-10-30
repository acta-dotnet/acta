using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acta.Verify;

/// <summary>
/// Builds a run's plan from the written standard this executable carries: the harness constants in
/// <c>Protocol/verify.json</c>, the scenario files, and the suites that order them.
/// </summary>
public static class Protocol
{
    public const string Id = "acta-verify/2";

    /// <summary>Every database, fastest first: a run measures them in this order.</summary>
    public static readonly string[] AllProviders = ["pg", "mssql", "sqlite"];

    public static string Read(string name) => Resource($"Acta.Verify.Protocol.{name}");

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The plan for <paramref name="suiteName"/> on <paramref name="providers"/>: the suite's scenarios in its order, or
    /// only <paramref name="selected"/> ones, run with the suite's timings and overrides.
    /// </summary>
    public static RunPlan Plan(string suiteName, string[] providers, int seed, string[]? selected = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (!ScenarioFiles.SuiteNames().Contains(suiteName, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown suite {suiteName}; choose {string.Join(", ", ScenarioFiles.SuiteNames())}.");
        }
        if (providers.Length == 0 || providers.Any(p => !AllProviders.Contains(p)))
        {
            throw new ArgumentException("Choose sqlite, pg, mssql, or all.");
        }
        var suite = ScenarioFiles.Suite(suiteName);
        using var constants = JsonDocument.Parse(Read("verify.json"));
        var all = ScenarioFiles.All(suite).ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var scenario in all.Values)
        {
            ScenarioRunner.Validate(scenario);
        }
        if (suite.Scenarios.FirstOrDefault(name => !all.ContainsKey(name)) is { } missing)
        {
            throw new InvalidOperationException($"Suite {suiteName} names {missing}, which is not a scenario.");
        }
        if (selected is { Length: > 0 } && selected.FirstOrDefault(name => !all.ContainsKey(name)) is { } unknown)
        {
            throw new ArgumentException($"Unknown scenario {unknown}; acta-verify list names every one.");
        }
        IEnumerable<string> names = selected is { Length: > 0 }
            ? suite.Scenarios.Where(selected.Contains).Concat(selected.Except(suite.Scenarios))
            : suite.Scenarios;
        return new RunPlan(
            Id,
            StandardHash(),
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}",
            suiteName,
            seed,
            providers.Distinct(StringComparer.Ordinal).ToArray(),
            [.. names.Distinct(StringComparer.Ordinal).Select(name => all[name])],
            suite,
            constants.RootElement.Clone()
        );
    }

    /// <summary>The hash of the whole written standard: the constants, every scenario file, every suite, and the result format.</summary>
    public static string StandardHash() =>
        Hash(
            string.Concat(
                Resources("Acta.Verify.Protocol.")
                    .Concat(Resources("Acta.Verify.Scenarios."))
                    .Concat(Resources("Acta.Verify.Suites."))
                    .Select(r => r + "\n" + Resource(r))
            )
        );

    /// <summary>
    /// Accepts a plan only when it equals what this executable plans for its suite, databases, seed and scenarios. A
    /// replay may differ only in its run id and its database servers.
    /// </summary>
    public static void Validate(RunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var expected = Plan(plan.SuiteName, [.. plan.Providers], plan.Seed, [.. plan.Scenarios.Select(s => s.Id)]);
        var comparable = plan with { RunId = expected.RunId, UseComposeServers = expected.UseComposeServers };
        if (
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(comparable, VerifyJson.Default.RunPlan),
                JsonSerializer.SerializeToElement(expected, VerifyJson.Default.RunPlan)
            )
        )
        {
            throw new ArgumentException("Plan differs from what this executable plans for its suite, databases, seed and scenarios.");
        }
    }

    /// <summary>The embedded JSON resources under <paramref name="prefix"/>, in ordinal order.</summary>
    internal static IEnumerable<string> Resources(string prefix) =>
        typeof(Protocol)
            .Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith(prefix, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    /// <summary>An embedded resource's text with LF line ends, so its hash is the same on every platform.</summary>
    internal static string Resource(string name)
    {
        using var stream =
            typeof(Protocol).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
