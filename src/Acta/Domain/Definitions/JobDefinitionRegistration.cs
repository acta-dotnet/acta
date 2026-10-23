namespace Acta;

/// <summary>
/// What registration holds a definition to: the framework policy defaults a <see cref="JobDescriptor"/>
/// falls back to when its <c>[Job]</c> attribute leaves a <c>definitions</c> policy column unset (one home,
/// so the providers never drift on the resolved row shape; each must match what the JobAttribute docs
/// and entity invariants state), the bounds the operator override gate shares, and the descriptor checks.
/// </summary>
internal static class JobDefinitionRegistration
{
    /// <summary>
    /// Canonical default expression - parses exactly to Backoff.Default (initial 60s, max 86400s,
    /// multiplier 2.0000, jitter 0.1000). Spelled out rather than "1m..1d": a ranged expression
    /// silently implies x2 growth and 10% jitter, so the short form hid the actual policy from
    /// every reader of a definition row, the docs, and the dashboard.
    /// </summary>
    public const string DefaultBackoffExpression = "1m..1d x2 ~10%";
    public const int DefaultExecutionTimeoutSeconds = 5 * 60;
    public const int DefaultJobRetentionSeconds = 90 * 24 * 60 * 60;

    /// <summary>
    /// Ceiling for a per-attempt execution timeout: the largest whole-second delay
    /// <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts
    /// (int.MaxValue milliseconds, about 24.8 days). A larger value would throw after the job is
    /// claimed, turning one oversized policy value into a claim-and-crash loop.
    /// </summary>
    public const int MaxExecutionTimeoutSeconds = int.MaxValue / 1000;

    /// <summary>
    /// The band a concurrency limit must fall in, declared or overridden: the same 1..1024 range
    /// <c>JobsOptions.MaxConcurrentExecutors</c> allows, since a limit above the largest possible
    /// executor pool can never bind.
    /// </summary>
    public const int MinConcurrencyLimit = 1;
    public const int MaxConcurrencyLimit = 1024;

    /// <summary>
    /// The largest MaxAttempts a definition or override may declare: the ceiling <c>jobs_state.failure_count</c>
    /// saturates at, so a saturated count still reaches every budget.
    /// </summary>
    public const int MaxMaxAttempts = short.MaxValue;

    /// <summary>
    /// The longest a rate name may be, matching <c>definitions.rate_name</c> and the concurrency key it
    /// shares a lock-key budget with.
    /// </summary>
    public const int MaxRateNameLength = 128;

    /// <summary>A parseable backoff expression that fits its column.</summary>
    public static bool IsBackoff(string value) => Backoff.TryParse(value, out _) && value.Length <= ActaTextLimits.DefinitionBackoff;

    /// <summary>A runbook URL that fits its ASCII column whole: printable ASCII, never truncated.</summary>
    public static bool IsRunbookUrl(string value) => IsPrintableAscii(value, ActaTextLimits.DefinitionRunbookUrl);

    private static bool IsPrintableAscii(string value, int maxLength) =>
        value.Length <= maxLength && !value.AsSpan().ContainsAnyExceptInRange((char)0x20, (char)0x7E);

    /// <summary>
    /// The name a contract type is stored and compared by: <see cref="Type.FullName"/>, with each generic argument
    /// rendered the same way instead of assembly-qualified, so no assembly's version bump changes the name.
    /// </summary>
    public static string ContractTypeName(Type type) =>
        type.IsArray ? $"{ContractTypeName(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]"
        : type.IsConstructedGenericType
            ? $"{type.GetGenericTypeDefinition().FullName}[{string.Join(",", type.GenericTypeArguments.Select(ContractTypeName))}]"
        : type.FullName ?? type.Name;

    public static DefinitionContract ContractOf(JobDescriptor descriptor) =>
        new(
            InputTypeName: ContractTypeName(descriptor.InputType),
            OutputTypeName: descriptor.OutputType is null ? null : ContractTypeName(descriptor.OutputType),
            InputFormatId: descriptor.InputPayloadFormat.Id,
            InputFormatName: descriptor.InputPayloadFormat.Name,
            OutputFormatId: descriptor.OutputPayloadFormat?.Id ?? (byte)0,
            OutputFormatName: descriptor.OutputPayloadFormat?.Name ?? JobPayloadFormat.NoneName
        );

    /// <summary>
    /// The meter a definition spends from: its declared RateName, or its own name when it declares
    /// none, normalized the way the runner composes the bucket key. Grouping on this rather than on
    /// the declared key alone is what catches a definition named <c>stripe</c> colliding with another
    /// definition's <c>RateName = "stripe"</c>.
    /// </summary>
    public static string EffectiveRateName(string? rateName, string name) => (rateName ?? name).ToLowerInvariant();

    /// <summary>
    /// Rejects a descriptor whose backoff, timeout, runbook URL, display name or description exceeds
    /// the bounds the operator override gate enforces, so the two surfaces share one contract, or whose
    /// contract type name does not fit its ASCII column. Declared text is rejected rather than
    /// truncated: the attribute is the developer's to fix.
    /// </summary>
    public static void ValidateDescriptorShape(JobDescriptor descriptor, string namespaceLabel)
    {
        // Hand-authored IJobManifest descriptors bypass the generator's compile-time Backoff check, so
        // this is the last gate before an invalid expression reaches the DB and crash-loops every
        // execution.
        var backoff = descriptor.Backoff ?? DefaultBackoffExpression;
        if (!IsBackoff(backoff))
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) has an invalid Backoff expression "
                    + $"\"{backoff}\": it must be a valid Acta backoff expression of at most {ActaTextLimits.DefinitionBackoff} characters."
            );
        }

        if (descriptor.MaxAttempts is < 1 or > MaxMaxAttempts)
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) declares MaxAttempts "
                    + $"{descriptor.MaxAttempts}: it must be between 1 and {MaxMaxAttempts}."
            );
        }

        // A hand-authored IJobManifest descriptor bypasses the generator's compile-time range check,
        // so this is the last gate before an unsatisfiable slot count reaches the definitions row.
        if (descriptor.ConcurrencyLimit is < MinConcurrencyLimit or > MaxConcurrencyLimit)
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) declares a ConcurrencyLimit of "
                    + $"{descriptor.ConcurrencyLimit}: it must be between {MinConcurrencyLimit} and {MaxConcurrencyLimit}."
            );
        }

        // Same last gate for the rate pair: the generator checks a declared rate at compile time, but a
        // hand-authored IJobManifest descriptor reaches registration unchecked.
        if (descriptor.RateLimit is { } rateLimit && !RateLimitSpec.TryParse(rateLimit, out _, out var rateError))
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) declares a RateLimit of "
                    + $"\"{rateLimit}\", which is not a rate limit. {rateError}"
            );
        }
        if (descriptor.RateName is { } rateName)
        {
            _ = NameSyntax.Canonicalize(rateName, nameof(descriptor.RateName), MaxRateNameLength);
        }

        if (descriptor.Lane is { } lane)
        {
            _ = NameSyntax.Canonicalize(lane, nameof(descriptor.Lane), NameSyntax.ExtendedMaxLength, reserveSystem: true);
            if (!descriptor.Schedules.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) declares the lane \"{lane}\" and a schedule. "
                        + "A recurring slot never finishes, so it would hold its lane forever; drop the lane, or enqueue laned work "
                        + "from the scheduled job."
                );
            }
        }

        var executionTimeout = descriptor.ExecutionTimeoutSeconds ?? DefaultExecutionTimeoutSeconds;
        if (executionTimeout > MaxExecutionTimeoutSeconds)
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) declares an ExecutionTimeout of {executionTimeout} seconds: "
                    + $"the ceiling is {MaxExecutionTimeoutSeconds} seconds."
            );
        }
        var contract = ContractOf(descriptor);
        RequireStorableTypeName("input", contract.InputTypeName);
        if (contract.OutputTypeName is { } outputTypeName)
        {
            RequireStorableTypeName("output", outputTypeName);
        }
        if (descriptor.RunbookUrl is { } runbookUrl && !IsRunbookUrl(runbookUrl))
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) has an invalid RunbookUrl: "
                    + $"it must be at most {ActaTextLimits.DefinitionRunbookUrl} printable ASCII characters."
            );
        }
        if (descriptor.DisplayName is { Length: > ActaTextLimits.DefinitionDisplayName })
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) has a DisplayName longer than {ActaTextLimits.DefinitionDisplayName} characters."
            );
        }
        if (descriptor.Description is { Length: > ActaTextLimits.DefinitionDescription })
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) has a Description longer than {ActaTextLimits.DefinitionDescription} characters."
            );
        }

        // SQL Server stores the name in a VARCHAR column, which cannot keep a character outside its code page.
        void RequireStorableTypeName(string side, string typeName)
        {
            if (!IsPrintableAscii(typeName, ActaTextLimits.DefinitionTypeName))
            {
                throw new ArgumentException(
                    $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) has the {side} type \"{typeName}\": a contract "
                        + $"type name must be at most {ActaTextLimits.DefinitionTypeName} printable ASCII characters. Rename the type, "
                        + "its namespace, or its generic arguments."
                );
            }
        }
    }
}
