namespace Acta;

/// <summary>
/// <see cref="IDefinitions.GetAsync"/>: resolves the natural key, then reads the row by id and gathers its override columns into
/// the one overrides value the update takes.
/// </summary>
internal sealed class GetDefinitionHandler(GetDefinitionIdHandler getDefinitionId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetDefinitionResponse?> HandleAsync(GetDefinitionQuery query, CancellationToken ct)
    {
        if (
            await getDefinitionId.HandleAsync(new GetDefinitionIdQuery(query.JobNamespace, query.JobName), ct)
            is not { DefinitionId: var definitionId }
        )
        {
            return null;
        }

        var row = await session.QuerySingleAsync(
            "GetDefinition",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DefinitionId, definitionId)),
            DbProjectionResolver.Resolve<GetDefinitionRow>(),
            ct
        );
        if (row is null)
        {
            return null;
        }

        return new GetDefinitionResponse(
            row.DefinitionId,
            row.JobNamespace,
            row.JobName,
            row.Status,
            row.DefinitionHash,
            row.ManifestGenerationAtUtc,
            row.InputTypeName,
            row.InputFormatId,
            row.InputFormatName,
            row.ResultTypeName,
            row.ResultFormatId,
            row.ResultFormatName,
            row.Priority,
            row.PriorityEffective,
            row.MaxAttempts,
            row.MaxAttemptsEffective,
            row.ConcurrencyLimit,
            row.ConcurrencyLimitEffective,
            row.RateLimit,
            row.RateLimitEffective,
            row.RateName,
            row.Lane,
            row.Backoff,
            row.BackoffEffective,
            row.ExecutionTimeoutSeconds,
            row.ExecutionTimeoutSecondsEffective,
            row.DeadlineSeconds,
            row.DeadlineSecondsEffective,
            row.DeadlineBehavior,
            row.DeadlineBehaviorEffective,
            row.JobRetentionSeconds,
            row.JobRetentionSecondsEffective,
            row.EventRecording,
            row.EventRecordingEffective,
            row.AlertProfile,
            row.AlertProfileEffective,
            row.FiringFailurePolicy,
            row.ScheduledDelivery,
            row.AlertChannelName,
            row.AlertChannelNameEffective,
            row.RunbookUrl,
            row.RunbookUrlEffective,
            row.DisplayName,
            row.DisplayNameEffective,
            row.Description,
            row.DescriptionEffective,
            new DefinitionPolicyOverrides(
                row.PriorityOverride,
                row.MaxAttemptsOverride,
                row.ConcurrencyLimitOverride,
                row.RateLimitOverride,
                row.BackoffOverride,
                row.ExecutionTimeoutSecondsOverride,
                row.DeadlineSecondsOverride,
                row.DeadlineBehaviorOverride,
                row.JobRetentionSecondsOverride,
                row.EventRecordingOverride,
                row.AlertProfileOverride,
                row.AlertChannelNameOverride,
                row.RunbookUrlOverride,
                row.DisplayNameOverride,
                row.DescriptionOverride
            ),
            row.CreatedAtUtc,
            row.ModifiedAtUtc,
            row.Version
        );
    }
}
