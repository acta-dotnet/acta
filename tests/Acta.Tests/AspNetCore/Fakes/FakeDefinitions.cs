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

            var (action, version) = Outcome(jobName, expectedVersion);
            return ValueTask.FromResult(new UpdateDefinitionOverridesResponse(action, version));
        };
        Retire = static (_, jobName, expectedVersion, _, _, _) =>
        {
            var (action, version) = Outcome(jobName, expectedVersion);
            return ValueTask.FromResult(new RetireDefinitionResponse(action, version));
        };
        Get = static (jobNamespace, jobName, _) => ValueTask.FromResult(Detail(jobNamespace, jobName));
        List = static (query, _) =>
            ValueTask.FromResult(new ListDefinitionsResponse(query.JobNamespace == "billing" ? [Item] : [], null, false, 50, null));
        GetInputTemplate = InputTemplate;
    }

    public Func<
        string,
        string,
        int,
        DefinitionPolicyOverrides,
        string?,
        string?,
        CancellationToken,
        ValueTask<UpdateDefinitionOverridesResponse>
    > UpdateOverrides { get; set; }

    public Func<string, string, int, string?, string?, CancellationToken, ValueTask<RetireDefinitionResponse>> Retire { get; set; }

    public Func<string, string, CancellationToken, ValueTask<GetDefinitionResponse?>> Get { get; set; }

    public Func<ListDefinitionsQuery, CancellationToken, ValueTask<ListDefinitionsResponse>> List { get; set; }

    public Func<string, string, GetDefinitionInputTemplateResponse?> GetInputTemplate { get; set; }

    ValueTask<UpdateDefinitionOverridesResponse> IDefinitions.UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => UpdateOverrides(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actor, ct);

    ValueTask<RetireDefinitionResponse> IDefinitions.RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Retire(jobNamespace, jobName, expectedVersion, reasonMessage, actor, ct);

    ValueTask<GetDefinitionResponse?> IDefinitions.GetAsync(string jobNamespace, string jobName, CancellationToken ct) =>
        Get(jobNamespace, jobName, ct);

    ValueTask<ListDefinitionsResponse> IDefinitions.ListAsync(ListDefinitionsQuery query, CancellationToken ct) => List(query, ct);

    GetDefinitionInputTemplateResponse? IDefinitions.GetInputTemplate(string jobNamespace, string jobName) =>
        GetInputTemplate(jobNamespace, jobName);

    /// <summary>The outcome and the version after it: the definition's current version on a conflict, one past the expected on an apply.</summary>
    private static (ControlAction Action, int? Version) Outcome(string jobName, int expectedVersion) =>
        jobName == "missing" ? (ControlAction.NotFound, null)
        : expectedVersion == 999 ? (ControlAction.VersionConflict, SendInvoice.Version)
        : (ControlAction.Applied, expectedVersion + 1);

    private static GetDefinitionResponse? Detail(string jobNamespace, string jobName) =>
        IsSendInvoice(jobNamespace, jobName) ? SendInvoice : null;

    /// <summary>Only ("billing", "send-invoice") is known to this fake host; anything else is unregistered.</summary>
    private static GetDefinitionInputTemplateResponse? InputTemplate(string jobNamespace, string jobName) =>
        IsSendInvoice(jobNamespace, jobName)
            ? new GetDefinitionInputTemplateResponse(
                "billing",
                "send-invoice",
                "Billing.SendInvoice",
                JobPayloadFormat.Json,
                """{"invoiceId":0,"note":null}"""
            )
            : null;

    /// <summary>
    /// Folds and validates the key before it resolves, as GetDefinitionId and the input template read do, so a malformed
    /// key (a wildcard) is an InvalidQueryException the edge maps to 400, never a miss.
    /// </summary>
    private static bool IsSendInvoice(string jobNamespace, string jobName)
    {
        try
        {
            jobNamespace = NameSyntax.Canonicalize(jobNamespace, nameof(jobNamespace));
            jobName = NameSyntax.Canonicalize(jobName, nameof(jobName), NameSyntax.ExtendedMaxLength);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidQueryException(ex.Message);
        }

        return jobNamespace == "billing" && jobName == "send-invoice";
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

    private static readonly GetDefinitionResponse SendInvoice = new(
        DefinitionId: 5,
        JobNamespace: "billing",
        JobName: "send-invoice",
        Status: DefinitionStatusCode.Active,
        DefinitionHash: "hash-5",
        CreatedDeploymentVersion: "1.4.0+b81d04e",
        InputTypeName: "Billing.SendInvoice",
        InputFormatId: 1,
        InputFormatName: "json",
        ResultTypeName: null,
        ResultFormatId: 0,
        ResultFormatName: "none",
        Priority: JobPriorityCode.Normal,
        PriorityEffective: JobPriorityCode.Normal,
        MaxAttempts: 3,
        MaxAttemptsEffective: 3,
        ConcurrencyLimit: null,
        ConcurrencyLimitEffective: null,
        RateLimit: null,
        RateLimitEffective: null,
        RateName: null,
        Lane: null,
        Backoff: "1s..1m x2",
        BackoffEffective: "1s..1m x2",
        ExecutionTimeoutSeconds: 60,
        ExecutionTimeoutSecondsEffective: 60,
        DeadlineSeconds: 0,
        DeadlineSecondsEffective: 0,
        DeadlineBehavior: DeadlineBehaviorCode.Strict,
        DeadlineBehaviorEffective: DeadlineBehaviorCode.Strict,
        JobRetentionSeconds: 0,
        JobRetentionSecondsEffective: 0,
        EventRecording: EventRecordingCode.Audit,
        EventRecordingEffective: EventRecordingCode.Audit,
        AlertProfile: AlertProfileCode.None,
        AlertProfileEffective: AlertProfileCode.None,
        FiringFailurePolicy: FiringFailurePolicyCode.Continue,
        ScheduledDelivery: ScheduledDeliveryCode.Permanent,
        AlertChannelName: null,
        AlertChannelNameEffective: null,
        RunbookUrl: null,
        RunbookUrlEffective: null,
        DisplayName: null,
        DisplayNameEffective: null,
        Description: null,
        DescriptionEffective: null,
        Overrides: new DefinitionPolicyOverrides(),
        CreatedAtUtc: CreatedAtUtc,
        ModifiedAtUtc: CreatedAtUtc,
        Version: 2
    );
}
