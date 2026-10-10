using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="DefinitionsOperations.RegisterDefinitionsAsync"/>: resolves descriptors to rows, then skips the upsert
/// entirely when nothing is new or changed.
/// </summary>
internal sealed class RegisterDefinitionsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<RegisterDefinitionsResponse> HandleAsync(RegisterDefinitionsCommand command, CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (command.Descriptors.IsDefaultOrEmpty)
        {
            return new RegisterDefinitionsResponse(result);
        }

        var rows = new List<DefinitionValues>(command.Descriptors.Length);
        foreach (var descriptor in command.Descriptors)
        {
            rows.Add(BuildRow(descriptor, command.NamespaceId));
        }

        ValidateSharedRateNames(rows, command.NamespaceId);

        var storedByName = new Dictionary<string, StoredDefinitionContract>(command.Stored.Count, StringComparer.Ordinal);
        foreach (var s in command.Stored)
        {
            storedByName[s.Name] = s;
        }

        ValidateJoiningRatesMatchOverriddenMeters(rows, storedByName, command.NamespaceId);

        var anyChange = false;
        foreach (var row in rows)
        {
            if (!storedByName.TryGetValue(row.Name, out var s))
            {
                anyChange = true; // new
            }
            else if (s.DefinitionHash != row.DefinitionHash || s.Status != DefinitionStatusCode.Active)
            {
                anyChange = true; // changed or needs reactivation
            }
        }

        if (!anyChange)
        {
            // Nothing to write. Every descriptor is present and Active in the stored set, so the id
            // map comes straight from the read.
            foreach (var row in rows)
            {
                result[row.Name] = storedByName[row.Name].Id;
            }

            return new RegisterDefinitionsResponse(result);
        }

        var registered = await session.ExecuteAsync(
            new StoreCommand("RegisterDefinitions"),
            cmd => dialect.BindRegisterDefinitions(cmd, command.NamespaceId, command.ManifestGenerationUtc, rows, session.Schema),
            DbProjectionResolver.Resolve<RegisterDefinitionsRow>(),
            ct
        );
        foreach (var (name, id) in registered)
        {
            result[name] = id;
        }

        return result.Count != rows.Count
            ? throw new InvalidOperationException(
                $"register_definitions returned {result.Count} name-to-id rows for {rows.Count} input definitions. "
                    + "The routine must return exactly one row per registered definition."
            )
            : new RegisterDefinitionsResponse(result);
    }

    /// <summary>
    /// Rejects a definition new to a meter whose stored participants carry an override unless it declares
    /// the meter's effective rate (override ?? declared): otherwise the join would desync a meter every other
    /// participant agrees on, or silently ride an operator's retune. Only joining is checked - a name absent
    /// from the stored catalog - so a restart never re-litigates what an earlier one, or an override since,
    /// made stick; that keeps <see cref="SetDefinitionOverridesHandler"/> free to retune a meter.
    /// </summary>
    private static void ValidateJoiningRatesMatchOverriddenMeters(
        List<DefinitionValues> rows,
        Dictionary<string, StoredDefinitionContract> storedByName,
        int namespaceId
    )
    {
        foreach (var row in rows)
        {
            if (row.RateLimit is not { } declared || storedByName.ContainsKey(row.Name))
            {
                continue;
            }

            var meter = JobDefinitionRegistration.EffectiveRateName(row.RateName, row.Name);
            foreach (var stored in storedByName.Values)
            {
                if (
                    stored.Effective.RateLimit is not { } effective
                    || string.Equals(effective, declared, StringComparison.Ordinal)
                    || !string.Equals(
                        JobDefinitionRegistration.EffectiveRateName(stored.Effective.RateName, stored.Name),
                        meter,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                throw new ArgumentException(
                    $"Job definition \"{row.Name}\" (namespace {namespaceId.ToString(CultureInfo.InvariantCulture)}) declares a "
                        + $"RateLimit of \"{declared}\", but it is joining meter \"{meter}\", whose effective rate is \"{effective}\": "
                        + "declare the meter's effective rate, or clear the override on it first."
                );
            }
        }
    }

    /// <summary>
    /// Rejects a namespace whose definitions disagree about the rate on one meter. A meter cannot run
    /// at two rates: whichever definition's worker asked last would set the interval, so the realized
    /// rate would depend on arrival order rather than on anything declared. Only definitions that
    /// declare a rate take part, because one that declares none never asks the meter anything.
    /// </summary>
    private static void ValidateSharedRateNames(List<DefinitionValues> rows, int namespaceId)
    {
        var rateByKey = new Dictionary<string, (string Name, string RateLimit)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.RateLimit is not { } rateLimit)
            {
                continue;
            }
            var key = JobDefinitionRegistration.EffectiveRateName(row.RateName, row.Name);
            if (!rateByKey.TryGetValue(key, out var first))
            {
                rateByKey[key] = (row.Name, rateLimit);
                continue;
            }
            if (!string.Equals(first.RateLimit, rateLimit, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Job definitions \"{first.Name}\" and \"{row.Name}\" (namespace {namespaceId.ToString(CultureInfo.InvariantCulture)}) "
                        + $"both meter on \"{key}\" but declare different rate limits "
                        + $"(\"{first.RateLimit}\" and \"{rateLimit}\"): definitions on one meter must declare the same rate."
                );
            }
        }
    }

    private static DefinitionValues BuildRow(JobDescriptor descriptor, int namespaceId)
    {
        var priorityCode = (byte)descriptor.Priority;
        var maxAttempts = descriptor.MaxAttempts;
        var concurrencyLimit = descriptor.ConcurrencyLimit;
        // Stored canonical so two spellings of one rate ("10/s" and " 10/s") are one value on a shared
        // key and one hash. Null stays null: no rate is a real state, not a rate of zero.
        var rateLimit = descriptor.Rate?.Text;
        var rateName = descriptor.RateName is { } declaredRateName
            ? NameSyntax.Canonicalize(declaredRateName, nameof(descriptor.RateName), JobDefinitionRegistration.MaxRateNameLength)
            : null;
        var lane = descriptor.Lane is { } declaredLane
            ? NameSyntax.Canonicalize(declaredLane, nameof(descriptor.Lane), NameSyntax.ExtendedMaxLength, reserveSystem: true)
            : null;
        var backoff = descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression;
        var executionTimeout = descriptor.ExecutionTimeoutSeconds ?? JobDefinitionRegistration.DefaultExecutionTimeoutSeconds;
        var deadlineSeconds = descriptor.DeadlineSeconds ?? 0;
        var deadlineBehaviorCode = (byte)descriptor.DeadlineBehavior;
        var jobRetention = descriptor.JobRetentionSeconds ?? JobDefinitionRegistration.DefaultJobRetentionSeconds;
        var auditLevelCode = (byte)descriptor.AuditLevel;
        var alertProfileCode = (byte)descriptor.AlertProfile;
        var tenantRequirementCode = (byte)descriptor.TenantRequirement;
        var alertChannelName = descriptor.AlertChannelName;
        var runbookUrl = descriptor.RunbookUrl;
        var displayName = descriptor.DisplayName;
        var description = descriptor.Description;
        var contract = JobDefinitionRegistration.ContractOf(descriptor);

        JobDefinitionRegistration.ValidateDescriptorShape(descriptor, namespaceId.ToString(CultureInfo.InvariantCulture));

        // definition_hash covers ALL code-owned columns (policy defaults + contract + formats) so one
        // C#-side comparison decides "needs upsert". Operator override columns are deliberately NOT
        // hashed, so overrides survive re-sync. ContractDriftDetector remains a separate gate that
        // compares actual contract values for the Warn/Fail policy.
        var c = CultureInfo.InvariantCulture;
        var definitionHash = CatalogHash.Of(
            priorityCode.ToString(c),
            maxAttempts.ToString(c),
            concurrencyLimit?.ToString(c),
            rateLimit,
            rateName,
            backoff,
            executionTimeout.ToString(c),
            deadlineSeconds.ToString(c),
            deadlineBehaviorCode.ToString(c),
            jobRetention.ToString(c),
            auditLevelCode.ToString(c),
            alertProfileCode.ToString(c),
            tenantRequirementCode.ToString(c),
            alertChannelName,
            runbookUrl,
            displayName,
            description,
            contract.InputTypeName,
            contract.OutputTypeName,
            contract.InputFormatId.ToString(c),
            contract.InputFormatName,
            contract.OutputFormatId.ToString(c),
            contract.OutputFormatName,
            lane
        );

        return new DefinitionValues(
            Name: descriptor.JobName,
            PriorityCode: priorityCode,
            MaxAttempts: maxAttempts,
            ConcurrencyLimit: concurrencyLimit,
            RateLimit: rateLimit,
            RateName: rateName,
            Lane: lane,
            Backoff: backoff,
            ExecutionTimeoutSeconds: executionTimeout,
            DeadlineSeconds: deadlineSeconds,
            DeadlineBehaviorCode: deadlineBehaviorCode,
            JobRetentionSeconds: jobRetention,
            InputTypeName: contract.InputTypeName,
            OutputTypeName: contract.OutputTypeName,
            InputFormatId: contract.InputFormatId,
            InputFormatName: contract.InputFormatName,
            OutputFormatId: contract.OutputFormatId,
            OutputFormatName: contract.OutputFormatName,
            AuditLevelCode: auditLevelCode,
            AlertProfileCode: alertProfileCode,
            TenantRequirementCode: tenantRequirementCode,
            AlertChannelName: alertChannelName,
            RunbookUrl: runbookUrl,
            DisplayName: displayName,
            Description: description,
            DefinitionHash: definitionHash
        );
    }
}
