namespace Acta.Runtime;

/// <summary>
/// <see cref="IDefinitions.UpdateOverridesAsync"/>: validates the override set, resolves the natural key,
/// then writes only the override columns, version-guarded, with the policy-changed event. When the
/// definition is on a rate meter, the routine carries the same rate override to every definition sharing it.
/// </summary>
internal sealed class SetJobDefinitionOverridesHandler(DefinitionKeyResolution keys, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<SetJobDefinitionOverridesResponse> HandleAsync(SetJobDefinitionOverridesCommand command, CancellationToken ct)
    {
        var overrides = Normalize(command.Overrides);
        var definitionId = await keys.ResolveAsync(command.JobNamespace, command.JobName, ct);
        if (definitionId is null)
        {
            return new SetJobDefinitionOverridesResponse(ControlAction.NotFound);
        }

        var actor = JobControlActor.Operator(command.ActorKey);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("SetJobDefinitionOverrides"),
            cmd =>
            {
                // The routine takes its arguments by position, so the order here is its signature's.
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.Id, definitionId.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.Version, command.ExpectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.PriorityCodeOverride, overrides.Priority));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.MaxAttemptsOverride, overrides.MaxAttempts));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.ConcurrencyLimitOverride, overrides.ConcurrencyLimit));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.RateLimitOverride, overrides.RateLimit));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.BackoffOverride, overrides.Backoff));
                cmd.Parameters.Add(
                    dialect.CreateParameter(ActaSchema.Definition.ExecutionTimeoutSecondsOverride, overrides.ExecutionTimeoutSeconds)
                );
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DeadlineSecondsOverride, overrides.DeadlineSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DeadlineBehaviorCodeOverride, overrides.DeadlineBehavior));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.RetentionSecondsOverride, overrides.JobRetentionSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.AuditLevelCodeOverride, overrides.AuditLevel));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.AlertProfileCodeOverride, overrides.AlertProfile));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.AlertChannelNameOverride, overrides.AlertChannelName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.RunbookUrlOverride, overrides.RunbookUrl));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DisplayNameOverride, overrides.DisplayName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DescriptionOverride, overrides.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ActorCode, actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ActorKey, actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonCode, EventReasonCode.JobControlManual));
                cmd.Parameters.Add(
                    dialect.CreateParameter(ActaSchema.Event.ReasonMessage, command.ReasonMessage.Truncate(ActaTextLimits.ReasonMessage))
                );
            },
            DbProjectionResolver.Resolve<SetJobDefinitionOverridesRow>(),
            ct
        );

        return new SetJobDefinitionOverridesResponse(row.Action.ToControlAction());
    }

    /// <summary>
    /// Rejects an override that would fail worse downstream, and canonicalizes the rest: the alert channel
    /// as kebab, and the free-form display name and description cut to their columns like every other
    /// operator-supplied message, so an over-length value never surfaces as a provider write error.
    /// </summary>
    internal static JobDefinitionPolicyOverrides Normalize(JobDefinitionPolicyOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        if (overrides.MaxAttempts is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrides), "MaxAttempts override must be at least 1.");
        }
        if (
            overrides.ConcurrencyLimit is < JobDefinitionRegistration.MinConcurrencyLimit or > JobDefinitionRegistration.MaxConcurrencyLimit
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(overrides),
                $"ConcurrencyLimit override must be between {JobDefinitionRegistration.MinConcurrencyLimit} "
                    + $"and {JobDefinitionRegistration.MaxConcurrencyLimit}."
            );
        }
        // A rate is a DSL value like Backoff, so a malformed override is REJECTED rather than coerced:
        // the meter would otherwise silently stop metering.
        if (overrides.RateLimit is { } rateLimit && !RateLimitSpec.TryParse(rateLimit, out _, out var rateError))
        {
            throw new ArgumentException($"RateLimit override \"{rateLimit}\" is not a rate limit. {rateError}", nameof(overrides));
        }
        if (overrides.ExecutionTimeoutSeconds is <= 0 or > JobDefinitionRegistration.MaxExecutionTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overrides),
                $"ExecutionTimeoutSeconds override must be between 1 and {JobDefinitionRegistration.MaxExecutionTimeoutSeconds}."
            );
        }
        if (overrides.DeadlineSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrides), "DeadlineSeconds override cannot be negative.");
        }
        if (overrides.JobRetentionSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrides), "JobRetentionSeconds override cannot be negative.");
        }

        if (overrides.AlertChannelName is { } alertChannelName)
        {
            overrides = overrides with
            {
                AlertChannelName = IdentifierSyntax.CanonicalizeKebab(
                    alertChannelName,
                    nameof(overrides.AlertChannelName),
                    ActaTextLimits.AlertChannelName
                ),
            };
        }

        // Backoff is a DSL expression, not a canonicalized identifier: an invalid or over-length value
        // is REJECTED outright (never truncated or silently coerced) so a bad override never lands.
        if (overrides.Backoff is { } backoff && !JobDefinitionRegistration.IsBackoff(backoff))
        {
            throw new ArgumentException(
                $"Backoff override \"{backoff}\" must be a valid Acta backoff expression of at most {ActaTextLimits.DefinitionBackoff} characters.",
                nameof(overrides)
            );
        }

        // RunbookUrl is a link an operator will click from an alert, bound for an ASCII column: a
        // truncated URL is a broken one and a non-ASCII value fails or mangles per provider, so both
        // are rejected like Backoff rather than silently coerced.
        if (overrides.RunbookUrl is { } url && !JobDefinitionRegistration.IsRunbookUrl(url))
        {
            throw new ArgumentException(
                $"RunbookUrl override must be at most {ActaTextLimits.DefinitionRunbookUrl} printable ASCII characters.",
                nameof(overrides)
            );
        }

        return overrides with
        {
            DisplayName = overrides.DisplayName.Truncate(ActaTextLimits.DefinitionDisplayName),
            Description = overrides.Description.Truncate(ActaTextLimits.DefinitionDescription),
        };
    }
}
