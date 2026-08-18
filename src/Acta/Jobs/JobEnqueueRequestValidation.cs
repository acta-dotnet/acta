namespace Acta;

internal static class JobEnqueueRequestValidation
{
    public static JobEnqueueRequest NormalizeAndValidate(JobEnqueueRequest request, string paramName)
    {
        ArgumentNullException.ThrowIfNull(request, paramName);

        ValidateSchedule(request, paramName);
        ValidateParentId(request, paramName);
        ValidatePriority(request, paramName);

        var correlationKey = request.CorrelationKey;
        if (correlationKey is not null)
        {
            IdentifierSyntax.ValidateExternalToken(
                correlationKey,
                Field(paramName, nameof(JobEnqueueRequest.CorrelationKey)),
                IdentifierSyntax.DefaultMaxLength
            );
        }

        return request with
        {
            JobNamespace = IdentifierSyntax.CanonicalizeUserKebab(
                request.JobNamespace,
                Field(paramName, nameof(JobEnqueueRequest.JobNamespace))
            ),
            JobName = IdentifierSyntax.CanonicalizeUserKebab(
                request.JobName,
                Field(paramName, nameof(JobEnqueueRequest.JobName)),
                IdentifierSyntax.ExtendedMaxLength
            ),
            DeduplicationKey = request.DeduplicationKey is null
                ? null
                : IdentifierSyntax.NormalizeKey(request.DeduplicationKey, Field(paramName, nameof(JobEnqueueRequest.DeduplicationKey))),
            ExclusiveKey = NormalizeOpaque(request.ExclusiveKey, Field(paramName, nameof(JobEnqueueRequest.ExclusiveKey))),
            TenantKey = request.TenantKey is null
                ? null
                : IdentifierSyntax.NormalizeTenantKey(request.TenantKey, Field(paramName, nameof(JobEnqueueRequest.TenantKey))),
            Tags = NormalizeTags(request.Tags, paramName),
        };
    }

    private static string? NormalizeOpaque(string? value, string paramName) =>
        value is null ? null : IdentifierSyntax.NormalizeKey(value, paramName);

    private static TagInput[]? NormalizeTags(IReadOnlyList<TagInput>? tags, string paramName)
    {
        if (tags is null)
        {
            return null;
        }

        if (tags.Count > TagLimits.MaxTagsPerTarget)
        {
            throw new ArgumentException(
                $"A job may carry at most {TagLimits.MaxTagsPerTarget} tags.",
                Field(paramName, nameof(JobEnqueueRequest.Tags))
            );
        }

        var normalized = new TagInput[tags.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tags.Count; i++)
        {
            normalized[i] = TagInput.Normalize(tags[i], Field(paramName, $"{nameof(JobEnqueueRequest.Tags)}[{i}]"));
            if (!names.Add(normalized[i].Name))
            {
                throw new ArgumentException(
                    $"Duplicate normalized tag name '{normalized[i].Name}'.",
                    Field(paramName, $"{nameof(JobEnqueueRequest.Tags)}[{i}].{nameof(TagInput.Name)}")
                );
            }
        }

        return normalized;
    }

    private static void ValidateSchedule(JobEnqueueRequest request, string paramName)
    {
        if (request.NextRunAtUtc is not null && request.DelaySeconds is not null)
        {
            throw new ArgumentException("Use NextRunAtUtc or DelaySeconds, not both.", paramName);
        }

        if (request.DelaySeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                Field(paramName, nameof(JobEnqueueRequest.DelaySeconds)),
                request.DelaySeconds,
                "DelaySeconds must be non-negative."
            );
        }
    }

    private static void ValidateParentId(JobEnqueueRequest request, string paramName)
    {
        if (request.ParentJobId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                Field(paramName, nameof(JobEnqueueRequest.ParentJobId)),
                request.ParentJobId,
                "ParentJobId must be positive."
            );
        }

        // The override is a cross-tenant CHILD opt-in: without a parent there is no tenant to cross,
        // and without an explicit key there is nothing to override with.
        if (request.OverrideParentTenant && (request.ParentJobId is null || request.TenantKey is null))
        {
            throw new ArgumentException(
                "OverrideParentTenant requires both a ParentJobId and an explicit TenantKey.",
                Field(paramName, nameof(JobEnqueueRequest.OverrideParentTenant))
            );
        }
    }

    private static void ValidatePriority(JobEnqueueRequest request, string paramName)
    {
        if (request.Priority is { } priority && !Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(
                Field(paramName, nameof(JobEnqueueRequest.Priority)),
                priority,
                "Priority is not a defined JobPriorityCode."
            );
        }
    }

    private static string Field(string paramName, string memberName) => $"{paramName}.{memberName}";
}
