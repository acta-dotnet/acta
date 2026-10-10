namespace Acta;

/// <summary>
/// Builds what the typed <see cref="IJobs"/> overloads pass on, and decodes what they read back. An enqueue request
/// resolves the route, serializes the input, and folds the options on; a None-format input carries
/// <see cref="JobPayload.None"/> with no serializer round-trip. A signal value serializes as JSON, and a typed read
/// decodes through its payload's own format.
/// </summary>
internal sealed class JobRequestRouting(
    IJobPayloadSerializerRegistry serializers,
    JobTypeRegistry typeRegistry,
    JobContractRegistry contractRegistry
)
{
    /// <summary>The request for an input whose CLR type maps to exactly one registered job.</summary>
    public EnqueueJobRequest FromInput<TInput>(TInput input, JobEnqueueOptions? options)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        var route = typeRegistry.Resolve(typeof(TInput), options?.JobNamespace);
        return Request(route.Namespace, route.JobName, Serialize(route.InputFormat, input), options);
    }

    /// <summary>
    /// The request for a generated manifest contract, checked against the registered descriptor so a hand-built
    /// contract with the wrong input or result type fails here. The no-input overload passes a default input.
    /// </summary>
    public EnqueueJobRequest FromContract<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options,
        Type? expectedResultType = null
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        var route = contractRegistry.Resolve(ValidateContract(job), job.JobName, options?.JobNamespace);
        if (route.InputType != typeof(TInput))
        {
            throw new ArgumentException(
                $"JobContract for '{route.JobName}' expects input '{route.InputType.FullName}' but was used with "
                    + $"'{typeof(TInput).FullName}'. Use the generated manifest member.",
                nameof(job)
            );
        }
        if (expectedResultType is not null && route.OutputType != expectedResultType)
        {
            throw new ArgumentException(
                $"JobContract for '{route.JobName}' produces result '{route.OutputType?.FullName ?? "none"}' but was used "
                    + $"with TResult '{expectedResultType.FullName}'.",
                nameof(job)
            );
        }

        return Request(route.Namespace, route.JobName, Serialize(route.InputFormat, input), options);
    }

    /// <summary>
    /// A typed signal value as JSON through the registry, which honours UseJsonPayloads, rather than the
    /// reflection-based static helper.
    /// </summary>
    public JobPayload Json<T>(T value) =>
        serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>A payload a typed read returns, decoded through the serializer of its own format.</summary>
    public T? Decode<T>(JobPayload payload) => serializers.Resolve(payload.Format.Id).Deserialize<T>(payload);

    private JobPayload Serialize<TInput>(JobPayloadFormat format, TInput input) =>
        format.IsNone ? JobPayload.None : serializers.Resolve(format.Id).Serialize(input);

    /// <summary>A default(JobContract&lt;T&gt;) is rejected here rather than by an incidental NullReferenceException downstream.</summary>
    private static Type ValidateContract<TInput>(JobContract<TInput> job)
    {
        if (job.ManifestType is null || string.IsNullOrWhiteSpace(job.JobName))
        {
            throw new ArgumentException("JobContract is uninitialized (default); use a generated manifest member.", nameof(job));
        }
        return !typeof(IJobManifest).IsAssignableFrom(job.ManifestType)
            ? throw new ArgumentException($"JobContract.ManifestType '{job.ManifestType.FullName}' is not an IJobManifest.", nameof(job))
            : job.ManifestType;
    }

    private static EnqueueJobRequest Request(string jobNamespace, string jobName, JobPayload input, JobEnqueueOptions? options) =>
        new(
            JobNamespace: jobNamespace,
            JobName: jobName,
            Input: input,
            DeduplicationKey: options?.DeduplicationKey,
            CorrelationKey: options?.CorrelationKey,
            ConcurrencyKey: options?.ConcurrencyKey,
            Priority: options?.Priority,
            Tags: options?.Tags,
            NextRunAtUtc: options?.NextRunAtUtc,
            DelaySeconds: options?.DelaySeconds,
            ParentJobId: options?.ParentJobId,
            TenantKey: options?.TenantKey,
            OverrideParentTenant: options?.OverrideParentTenant ?? false,
            Lane: options?.Lane
        );
}
