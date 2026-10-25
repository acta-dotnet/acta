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
    /// The request for a generated call, checked against the registered descriptor so a hand-built call with the wrong
    /// input type fails here.
    /// </summary>
    public EnqueueJobRequest FromCall<TInput>(JobCall<TInput> call, JobEnqueueOptions? options)
        where TInput : notnull => FromContract(call.Contract.ManifestType, call.Contract.JobName, call.Input, options, null);

    /// <summary>The request for a generated call of a job that returns a result, its result type checked too.</summary>
    public EnqueueJobRequest FromCall<TInput, TResult>(JobCall<TInput, TResult> call, JobEnqueueOptions? options)
        where TInput : notnull
        where TResult : notnull => FromContract(call.Contract.ManifestType, call.Contract.JobName, call.Input, options, typeof(TResult));

    private EnqueueJobRequest FromContract<TInput>(
        Type? manifestType,
        string? jobName,
        TInput input,
        JobEnqueueOptions? options,
        Type? expectedResultType
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        var route = contractRegistry.Resolve(ValidateContract(manifestType, jobName), jobName!, options?.JobNamespace);
        if (route.InputType != typeof(TInput))
        {
            throw new ArgumentException(
                $"The call of '{route.JobName}' carries input '{typeof(TInput).FullName}' but the job takes "
                    + $"'{route.InputType.FullName}'. Use the manifest's generated call."
            );
        }
        if (expectedResultType is not null && route.OutputType != expectedResultType)
        {
            throw new ArgumentException(
                $"The call of '{route.JobName}' expects result '{expectedResultType.FullName}' but the job produces "
                    + $"'{route.OutputType?.FullName ?? "none"}'."
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

    /// <summary>A default call is rejected here rather than by an incidental NullReferenceException downstream.</summary>
    private static Type ValidateContract(Type? manifestType, string? jobName)
    {
        if (manifestType is null || string.IsNullOrWhiteSpace(jobName))
        {
            throw new ArgumentException("The call is uninitialized (default); use the manifest's generated call.");
        }
        return !typeof(IJobManifest).IsAssignableFrom(manifestType)
            ? throw new ArgumentException($"The call's manifest type '{manifestType.FullName}' is not an IJobManifest.")
            : manifestType;
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
            TenantName: options?.TenantName,
            OverrideParentTenant: options?.OverrideParentTenant ?? false,
            Lane: options?.Lane
        );
}
