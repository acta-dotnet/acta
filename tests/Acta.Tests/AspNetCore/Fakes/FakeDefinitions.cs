namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="IDefinitions"/> over one definition, billing/send-invoice (id 5), the namespace and name the found job's
/// snapshot carries, so the definition link on the job screen addresses a real row. Both verbs answer a job name of
/// "missing" as not found and an expectedVersion of 999 as the stale CAS token.
/// </summary>
internal sealed class FakeDefinitions : IDefinitions
{
    private static readonly DateTime CreatedAtUtc = new(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc);

    public FakeDefinitions()
    {
        UpdateOverrides = static (_, jobName, expectedVersion, overrides, _, _, _) =>
        {
            // Mirror the production guard: an out-of-range override surfaces as ArgumentOutOfRangeException.
            if (overrides.MaxAttempts is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(overrides.MaxAttempts), "MaxAttempts override must be at least 1.");
            }

            return ValueTask.FromResult(new SetDefinitionOverridesResponse(Outcome(jobName, expectedVersion)));
        };
        Retire = static (_, jobName, expectedVersion, _, _, _) =>
            ValueTask.FromResult(new RetireDefinitionResponse(Outcome(jobName, expectedVersion)));
        Get = static (jobNamespace, jobName, _) => ValueTask.FromResult(Detail(jobNamespace, jobName));
        List = static (query, _) =>
            ValueTask.FromResult(new ListDefinitionsResponse(query.JobNamespace == "billing" ? [Item] : [], null, false, 50, null));
    }

    public Func<
        string,
        string,
        int,
        DefinitionPolicyOverrides,
        string?,
        string?,
        CancellationToken,
        ValueTask<SetDefinitionOverridesResponse>
    > UpdateOverrides { get; set; }

    public Func<string, string, int, string?, string?, CancellationToken, ValueTask<RetireDefinitionResponse>> Retire { get; set; }

    public Func<string, string, CancellationToken, ValueTask<DefinitionDetail?>> Get { get; set; }

    public Func<ListDefinitionsQuery, CancellationToken, ValueTask<ListDefinitionsResponse>> List { get; set; }

    ValueTask<SetDefinitionOverridesResponse> IDefinitions.UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => UpdateOverrides(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actorKey, ct);

    ValueTask<RetireDefinitionResponse> IDefinitions.RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Retire(jobNamespace, jobName, expectedVersion, reasonMessage, actorKey, ct);

    ValueTask<DefinitionDetail?> IDefinitions.GetAsync(string jobNamespace, string jobName, CancellationToken ct) =>
        Get(jobNamespace, jobName, ct);

    ValueTask<ListDefinitionsResponse> IDefinitions.ListAsync(ListDefinitionsQuery query, CancellationToken ct) => List(query, ct);

    private static ControlAction Outcome(string jobName, int expectedVersion) =>
        jobName == "missing" ? ControlAction.NotFound
        : expectedVersion == 999 ? ControlAction.Rejected
        : ControlAction.Applied;

    /// <summary>
    /// Validates the key before it resolves, as DefinitionKeyResolution does, so a non-canonical key (uppercase, a
    /// wildcard) is an InvalidQueryException the edge maps to 400, never a miss.
    /// </summary>
    private static DefinitionDetail? Detail(string jobNamespace, string jobName)
    {
        try
        {
            IdentifierSyntax.ValidateKebab(jobNamespace, nameof(jobNamespace));
            IdentifierSyntax.ValidateDottedKebab(jobName, nameof(jobName), IdentifierSyntax.ExtendedMaxLength);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidQueryException(ex.Message);
        }

        return jobNamespace == "billing" && jobName == "send-invoice" ? SendInvoice : null;
    }

    private static readonly ListDefinitionsItem Item = new(
        5,
        "billing",
        "send-invoice",
        DefinitionStatusCode.Active,
        "Billing.SendInvoice",
        null,
        null,
        JobPriorityCode.Normal,
        null,
        3,
        null,
        null,
        null,
        null,
        null,
        null,
        CreatedAtUtc,
        2
    );

    private static readonly DefinitionDetail SendInvoice = new(
        DefinitionId: 5,
        JobNamespace: "billing",
        JobName: "send-invoice",
        Status: DefinitionStatusCode.Active,
        DefinitionHash: "hash-5",
        ManifestGenerationAtUtc: CreatedAtUtc,
        InputTypeName: "Billing.SendInvoice",
        InputFormatId: 1,
        InputFormatName: "json",
        OutputTypeName: null,
        OutputFormatId: 0,
        OutputFormatName: "none",
        Priority: JobPriorityCode.Normal,
        PriorityOverride: null,
        PriorityEffective: JobPriorityCode.Normal,
        MaxAttempts: 3,
        MaxAttemptsOverride: null,
        MaxAttemptsEffective: 3,
        ConcurrencyLimit: null,
        ConcurrencyLimitOverride: null,
        ConcurrencyLimitEffective: null,
        RateLimit: null,
        RateLimitOverride: null,
        RateLimitEffective: null,
        RateKey: null,
        Lane: null,
        Backoff: "1s..1m x2",
        BackoffOverride: null,
        BackoffEffective: "1s..1m x2",
        ExecutionTimeoutSeconds: 60,
        ExecutionTimeoutSecondsOverride: null,
        ExecutionTimeoutSecondsEffective: 60,
        DeadlineSeconds: 0,
        DeadlineSecondsOverride: null,
        DeadlineSecondsEffective: 0,
        DeadlineBehavior: DeadlineBehaviorCode.Strict,
        DeadlineBehaviorOverride: null,
        DeadlineBehaviorEffective: DeadlineBehaviorCode.Strict,
        JobRetentionSeconds: 0,
        JobRetentionSecondsOverride: null,
        JobRetentionSecondsEffective: 0,
        AuditLevel: DefinitionAuditLevelCode.Audit,
        AuditLevelOverride: null,
        AuditLevelEffective: DefinitionAuditLevelCode.Audit,
        AlertProfile: AlertProfileCode.None,
        AlertProfileOverride: null,
        AlertProfileEffective: AlertProfileCode.None,
        AlertChannelName: null,
        AlertChannelNameOverride: null,
        AlertChannelNameEffective: null,
        RunbookUrl: null,
        RunbookUrlOverride: null,
        RunbookUrlEffective: null,
        DisplayName: null,
        DisplayNameOverride: null,
        DisplayNameEffective: null,
        Description: null,
        DescriptionOverride: null,
        DescriptionEffective: null,
        CreatedAtUtc: CreatedAtUtc,
        ModifiedAtUtc: CreatedAtUtc,
        Version: 2
    );
}
