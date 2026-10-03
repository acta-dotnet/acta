using Acta;

namespace Anvil.Bench;

/// <summary>
/// The known scenarios, by CLI name.
/// </summary>
public static class ScenarioRegistry
{
    public static readonly IReadOnlyList<IScenario> All =
    [
        new ThroughputScenario(),
        new DrainScenario(),
        new LatencyScenario(),
        new EnqueueScenario(),
        new EnqueueBatchScenario(),
        new RecoveryScenario(),
        new WakeupScenario(),
        new QueryScenario(),
        new PurgeScenario(),
        new LoadProfileScenario(),
        new RateScenario(),
        new LanesScenario(),
        new ClaimSkewScenario(),
    ];

    public static IScenario? Find(string name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
