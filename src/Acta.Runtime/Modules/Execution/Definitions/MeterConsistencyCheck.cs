using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution.Definitions;

/// <summary>
/// Startup and policy-reload consistency check for shared rate meters: groups a namespace's
/// definitions by effective meter key and warns, once per meter, when its participants disagree on
/// the effective rate (override ?? declared). A meter can still split by design - a manifest that
/// moves an overridden definition onto another meter, or a retune landing between a starting worker's
/// catalog read and its registration write - so this only reports it; retune by overriding any
/// participant, which carries the same rate to the rest of the meter.
/// </summary>
internal static class MeterConsistencyCheck
{
    public static void Check(string namespaceName, IReadOnlyList<StoredDefinitionContract> catalog, ILogger log)
    {
        var byMeter = new Dictionary<string, List<StoredDefinitionContract>>(StringComparer.Ordinal);
        foreach (var definition in catalog)
        {
            if (definition.Effective.RateLimit is null)
            {
                continue;
            }

            var meter = DefinitionsService.EffectiveRateKey(definition.Effective.RateKey, definition.Name);
            if (!byMeter.TryGetValue(meter, out var participants))
            {
                participants = [];
                byMeter[meter] = participants;
            }
            participants.Add(definition);
        }

        foreach (var (meter, participants) in byMeter)
        {
            if (participants.Select(p => p.Effective.RateLimit).Distinct(StringComparer.Ordinal).Count() <= 1)
            {
                continue;
            }

            var detail = $"{meter}: {string.Join(", ", participants.Select(p => $"{p.Name}={p.Effective.RateLimit}"))}";
            log.LogWarning(
                "Acta rate limiting: meter ({Detail}) has participants at different effective rates in namespace "
                    + "({Namespace}); a meter whose participants disagree is retuned by overriding any participant.",
                detail,
                namespaceName
            );
        }
    }
}
