using System.Collections.Immutable;
using System.Globalization;
using Acta.Features.Jobs;
using Acta.Features.Shared;
using Acta.Features.Tags;
using Acta.Payloads;
using Acta.Querying;

namespace Acta.Features.Definitions;

/// <summary>
/// Definitions feature behavior: dashboard read validation and cursor math, the operator override
/// write rules (canonicalization, backoff rejection, actor shaping), and the registration policy
/// (descriptor-to-row resolution, the definition hash, and the C#-side write gate that lets a
/// steady-state restart issue zero writes). Provider stores receive resolved rows and validated
/// commands; the database keeps the per-row generation/hash gate and retire-by-absence.
/// </summary>
internal sealed class DefinitionsService(IDefinitionStore store)
{
    private const string OrderDefinitions = "namespace asc, name asc, id asc";
    private const string ListOperationName = "ListJobDefinitions";

    public async ValueTask<JobDefinitionDetail?> GetAsync(int definitionId, CancellationToken ct)
    {
        QueryValidation.ValidatePositiveId(definitionId, nameof(definitionId));
        return await store.GetDefinitionAsync(definitionId, ct);
    }

    public async ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListJobDefinitionsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with { JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)) };
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));

        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListJobDefinitionsQuery));
        var filterHash = QueryFilterHash.Compute([("ns", query.JobNamespace), ("status", Num(query.Status)), ("tags", tagFilters)]);

        string? cursorNamespace = null;
        string? cursorName = null;
        int? cursorId = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(
                query.Cursor,
                ListOperationName,
                OrderDefinitions,
                filterHash,
                [CursorKeyKind.Text, CursorKeyKind.Text, CursorKeyKind.Int]
            );
            cursorNamespace = (string)keys[0];
            cursorName = (string)keys[1];
            cursorId = (int)keys[2];
        }

        var page = await store.ListDefinitionsAsync(
            new DefinitionPageRequest(
                query.JobNamespace,
                query.Status,
                cursorNamespace,
                cursorName,
                cursorId,
                pageSize + 1,
                query.IncludeTotal,
                tagFilters
            ),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;

        var nextCursor = hasMore
            ? PageCursorCodec.Encode(
                ListOperationName,
                OrderDefinitions,
                filterHash,
                [items[^1].JobNamespace, items[^1].JobName, items[^1].JobDefinitionId]
            )
            : null;

        return new PagedResult<JobDefinitionListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    public async ValueTask<DefinitionOverrideResult> SetOverridesAsync(
        int definitionId,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        string? actorKey,
        string? note,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(overrides);

        if (overrides.MaxAttempts is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrides.MaxAttempts), "MaxAttempts override must be at least 1.");
        }
        if (overrides.ExecutionTimeoutSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overrides.ExecutionTimeoutSeconds),
                "ExecutionTimeoutSeconds override must be positive."
            );
        }
        if (overrides.DeadlineSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrides.DeadlineSeconds), "DeadlineSeconds override cannot be negative.");
        }
        if (overrides.JobRetentionSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overrides.JobRetentionSeconds),
                "JobRetentionSeconds override cannot be negative."
            );
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
        if (overrides.Backoff is { } backoff)
        {
            var maxLength = ActaTextLimits.DefinitionBackoff;
            if (!Backoff.TryParse(backoff, out _) || backoff.Length > maxLength)
            {
                throw new ArgumentException(
                    $"Backoff override \"{backoff}\" must be a valid Acta backoff expression of at most {maxLength} characters.",
                    nameof(overrides.Backoff)
                );
            }
        }

        var actor = new JobControlActor(
            JobActorCode.Operator,
            JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey)
        );

        var outcome = await store.SetDefinitionOverridesAsync(
            new SetDefinitionOverridesCommand(definitionId, expectedVersion, overrides, actor, note.Truncate(ActaTextLimits.ReasonMessage)),
            ct
        );

        return new DefinitionOverrideResult(
            outcome.Action switch
            {
                DefinitionOverrideAction.Applied => JobControlAction.Applied,
                DefinitionOverrideAction.NotFound => JobControlAction.NotFound,
                _ => JobControlAction.Rejected,
            }
        );
    }

    /// <summary>
    /// Registers the namespace's whole definitions set: resolves descriptors to rows, then skips the
    /// upsert entirely when nothing is new, changed, or needs retiring - a steady-state restart issues
    /// zero writes and takes no locks. Returns a name-to-id map for every descriptor.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> RegisterAsync(
        short namespaceId,
        DateTime manifestGenerationUtc,
        ImmutableArray<JobDescriptor> descriptors,
        IReadOnlyList<StoredDefinitionContract> stored,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (descriptors.IsDefaultOrEmpty)
        {
            return result;
        }

        var rows = new List<JobDefinitionRow>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            rows.Add(BuildRow(descriptor, namespaceId));
        }

        var storedByName = new Dictionary<string, StoredDefinitionContract>(stored.Count, StringComparer.Ordinal);
        foreach (var s in stored)
        {
            storedByName[s.Name] = s;
        }

        var manifestNames = new HashSet<string>(rows.Count, StringComparer.Ordinal);
        var anyChange = false;
        foreach (var row in rows)
        {
            manifestNames.Add(row.Name);
            if (!storedByName.TryGetValue(row.Name, out var s))
            {
                anyChange = true; // new
            }
            else if (s.DefinitionHash != row.DefinitionHash || s.Status != JobDefinitionStatusCode.Active)
            {
                anyChange = true; // changed or needs reactivation
            }
        }

        if (!anyChange)
        {
            foreach (var s in stored)
            {
                if (s.Status == JobDefinitionStatusCode.Active && !manifestNames.Contains(s.Name))
                {
                    anyChange = true; // an active definition absent from the manifest must be retired
                    break;
                }
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

            return result;
        }

        var read = await store.RegisterDefinitionsAsync(new RegisterDefinitionsCommand(namespaceId, manifestGenerationUtc, rows), ct);

        foreach (var (name, id) in read)
        {
            result[name] = id;
        }

        if (result.Count != rows.Count)
        {
            throw new InvalidOperationException(
                $"register_job_definitions returned {result.Count} name-to-id rows for {rows.Count} input definitions. "
                    + "The routine must return exactly one row per registered definition."
            );
        }

        return result;
    }

    public static DefinitionContract ContractOf(JobDescriptor descriptor) =>
        new(
            InputTypeName: descriptor.InputType.FullName ?? descriptor.InputType.Name,
            OutputTypeName: descriptor.OutputType?.FullName,
            InputFormatId: descriptor.InputPayloadFormat.Id,
            InputFormatName: descriptor.InputPayloadFormat.Name,
            OutputFormatId: descriptor.OutputPayloadFormat?.Id ?? (byte)0,
            OutputFormatName: descriptor.OutputPayloadFormat?.Name ?? JobPayloadFormat.NoneName
        );

    private static JobDefinitionRow BuildRow(JobDescriptor descriptor, short namespaceId)
    {
        var priorityCode = (byte)descriptor.Priority;
        var maxAttempts = descriptor.MaxAttempts;
        var backoff = descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression;

        // Hand-authored IActaManifest descriptors bypass the generator's compile-time Backoff check, so
        // this is the only remaining gate before an invalid expression reaches the DB - mirrors the
        // override write gate in SetOverridesAsync so a bad value fails fast at worker init instead of
        // crash-looping every execution.
        var maxBackoffLength = ActaTextLimits.DefinitionBackoff;
        if (!Backoff.TryParse(backoff, out _) || backoff.Length > maxBackoffLength)
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceId}) has an invalid Backoff expression "
                    + $"\"{backoff}\": it must be a valid Acta backoff expression of at most {maxBackoffLength} characters."
            );
        }

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
        var contract = ContractOf(descriptor);

        // definition_hash covers ALL code-owned columns (policy defaults + contract + formats) so one
        // C#-side comparison decides "needs upsert". Operator override columns are deliberately NOT
        // hashed, so overrides survive re-sync. ContractDriftDetector remains a separate gate that
        // compares actual contract values for the Warn/Fail policy.
        var c = CultureInfo.InvariantCulture;
        var definitionHash = CatalogHash.Of(
            priorityCode.ToString(c),
            maxAttempts.ToString(c),
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
            contract.OutputFormatName
        );

        return new JobDefinitionRow(
            Name: descriptor.JobName,
            PriorityCode: priorityCode,
            MaxAttempts: maxAttempts,
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

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
