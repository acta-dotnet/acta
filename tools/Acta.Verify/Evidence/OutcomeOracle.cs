namespace Acta.Verify;

public sealed record ExpectedOutcome(
    string Key,
    string Status,
    string? Output,
    string? Parent = null,
    IReadOnlyList<string>? Steps = null,
    string? JobNamespace = null
);

public sealed record ObservedOutcome(
    string Key,
    string Status,
    string? Output,
    string? Parent = null,
    IReadOnlyList<string>? Steps = null,
    string? JobNamespace = null
);

public sealed record Check(string Code, string Identity, bool Passed, string Detail);

public sealed record FaultEvidence(
    string Target,
    IReadOnlyList<string> AffectedIds,
    bool BoundaryObserved,
    bool InjectionAcknowledged,
    bool ReclaimObserved,
    string? Provider = null,
    string? CaseId = null,
    int Repetition = 0,
    DateTimeOffset? RecordedAtUtc = null,
    string Event = "fault_boundary"
);

public static class OutcomeOracle
{
    public static Check[] SafetyFailures(
        IEnumerable<Check> checks,
        IReadOnlySet<string> unresolvedRoots,
        Func<string, bool> acknowledged
    ) =>
        checks
            .Where(c =>
                !c.Passed
                && (
                    c.Code
                        is "unexpected-identity"
                            or "unique-terminal"
                            or "parent-identity"
                            or "namespace-identity"
                            or "acknowledged-identity"
                            or "terminal-parent-live-child"
                            or "observed-terminal-outcome"
                    || !unresolvedRoots.Contains(c.Identity.Split('/')[0])
                    || c.Code == "missing-identity" && acknowledged(c.Identity)
                )
            )
            .ToArray();

    public static Check[] Reconcile(IEnumerable<ExpectedOutcome> expected, IEnumerable<ObservedOutcome> observed)
    {
        var model = expected.ToDictionary(e => e.Key, StringComparer.Ordinal);
        Dictionary<string, ObservedOutcome> actual = new(StringComparer.Ordinal);
        Dictionary<string, int> duplicates = new(StringComparer.Ordinal);
        foreach (var outcome in observed)
        {
            if (!actual.TryAdd(outcome.Key, outcome))
            {
                duplicates[outcome.Key] = duplicates.GetValueOrDefault(outcome.Key, 1) + 1;
            }
        }
        List<Check> checks = [];
        Dictionary<string, int> verified = new(StringComparer.Ordinal);
        foreach (var (key, wanted) in model)
        {
            if (!actual.TryGetValue(key, out var found))
            {
                checks.Add(new("missing-identity", key, false, "Expected durable outcome is missing."));
                continue;
            }
            var count = duplicates.GetValueOrDefault(key, 1);
            Record(new("unique-terminal", key, count == 1, $"Observed {count} authoritative outcomes."));
            var row = found;
            // "XOrSucceeded" allows the model's success or the non-success X, which carries no output and commits no
            // step the model does not predict.
            var alternative = wanted.Status == row.Status + "OrSucceeded";
            Record(
                new(
                    "terminal-status",
                    key,
                    wanted.Status == row.Status
                        || alternative
                        || wanted.Status.EndsWith("OrSucceeded", StringComparison.Ordinal) && row.Status == "Succeeded",
                    $"Expected {wanted.Status}; observed {row.Status}."
                )
            );
            Record(
                new(
                    "logical-result",
                    key,
                    alternative ? row.Output is null : wanted.Output == row.Output,
                    "Compare independently calculated output."
                )
            );
            Record(
                new(
                    "namespace-identity",
                    key,
                    wanted.JobNamespace is null || wanted.JobNamespace == row.JobNamespace,
                    "Compare the exact expected namespace."
                )
            );
            Record(new("parent-identity", key, wanted.Parent == row.Parent, "Compare exact graph edge."));
            Record(
                new(
                    "committed-steps",
                    key,
                    alternative
                        ? (row.Steps ?? [])
                            .Where(s => !s.EndsWith("=uncommitted", StringComparison.Ordinal))
                            .All(s => (wanted.Steps ?? []).Contains(s, StringComparer.Ordinal))
                        : (wanted.Steps ?? [])
                            .Order(StringComparer.Ordinal)
                            .SequenceEqual((row.Steps ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal),
                    "Compare exact checkpoint names and result digests."
                )
            );
        }
        foreach (var key in actual.Keys.Except(model.Keys, StringComparer.Ordinal))
        {
            checks.Add(new("unexpected-identity", key, false, "Identity was not in the frozen logical model."));
        }
        foreach (var (code, count) in verified)
        {
            checks.Add(
                new(
                    "verified-" + code,
                    "cohort",
                    true,
                    $"Verified {count} exact logical identities; all failures are retained individually."
                )
            );
        }
        return [.. checks];

        void Record(Check check)
        {
            if (check.Passed)
            {
                verified[check.Code] = verified.GetValueOrDefault(check.Code) + 1;
            }
            else
            {
                checks.Add(check);
            }
        }
    }

    public static bool FaultCovered(FaultEvidence evidence, bool requireReclaim)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence.BoundaryObserved
            && evidence.InjectionAcknowledged
            && evidence.AffectedIds.Count > 0
            && (!requireReclaim || evidence.ReclaimObserved);
    }
}
