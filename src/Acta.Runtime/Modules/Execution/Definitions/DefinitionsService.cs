using System.Collections.Immutable;
using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Querying;

namespace Acta.Runtime.Modules.Execution.Definitions;

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

    public async ValueTask<JobDefinitionDetail?> GetAsync(string jobNamespace, string jobName, CancellationToken ct)
    {
        var definitionId = await ResolveDefinitionIdAsync(jobNamespace, jobName, ct);
        return definitionId is null ? null : await store.GetDefinitionAsync(definitionId.Value, ct);
    }

    /// <summary>
    /// Resolves a definition's natural key (namespace + name) to its catalog id, or null when the
    /// catalog holds no such definition. The grid read is the resolver, and it is an exact match by
    /// construction twice over: <c>NameSearch</c> is a LIKE pattern whose <c>%</c> wrapping is applied
    /// by <see cref="ListAsync"/> and never by the store, so the bare name passed here carries no
    /// wildcard (<see cref="QueryValidation.ValidateJobNameFragment"/> rejects <c>%</c> and <c>_</c>),
    /// and the returned rows are still filtered on an ordinal name equality below. A prefix sibling
    /// (<c>invoice</c> beside <c>invoice-retry</c>) therefore cannot be resolved by accident.
    /// </summary>
    private async ValueTask<int?> ResolveDefinitionIdAsync(string jobNamespace, string jobName, CancellationToken ct)
    {
        var ns = QueryValidation.ValidateNamespace(jobNamespace, nameof(jobNamespace));
        var name = QueryValidation.ValidateJobNameFragment(jobName, nameof(jobName));
        if (ns is null || name is null)
        {
            throw new InvalidQueryException("jobNamespace and jobName are both required.");
        }

        var page = await store.ListDefinitionsAsync(
            new DefinitionPageRequest(
                JobNamespace: ns,
                NameSearch: name,
                Status: null,
                CursorNamespaceName: null,
                CursorJobName: null,
                CursorId: null,
                Take: 2,
                IncludeTotal: false
            ),
            ct
        );
        foreach (var row in page.Rows)
        {
            if (string.Equals(row.JobName, name, StringComparison.Ordinal))
            {
                return row.DefinitionId;
            }
        }

        return null;
    }

    public async ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with { JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)) };
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));
        query = query with { NameContains = QueryValidation.ValidateJobNameFragment(query.NameContains, nameof(query.NameContains)) };

        var nameSearch = query.NameContains is null ? null : "%" + query.NameContains + "%";
        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListDefinitionsQuery));
        var filterHash = QueryFilterHash.Compute([
            ("ns", query.JobNamespace),
            ("contains", query.NameContains),
            ("status", Num(query.Status)),
            ("tags", tagFilters),
        ]);

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
                nameSearch,
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
                [items[^1].JobNamespace, items[^1].JobName, items[^1].DefinitionId]
            )
            : null;

        return new PagedResult<JobDefinitionListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    public async ValueTask<DefinitionControlResult> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        string? actorKey,
        string? reasonMessage,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(overrides);

        if (overrides.MaxAttempts is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overrides), "MaxAttempts override must be at least 1.");
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
        if (overrides.Backoff is { } backoff)
        {
            var maxLength = ActaTextLimits.DefinitionBackoff;
            if (!Backoff.TryParse(backoff, out _) || backoff.Length > maxLength)
            {
                throw new ArgumentException(
                    $"Backoff override \"{backoff}\" must be a valid Acta backoff expression of at most {maxLength} characters.",
                    nameof(overrides)
                );
            }
        }

        // RunbookUrl is a link an operator will click from an alert, bound for an ASCII column: a
        // truncated URL is a broken one and a non-ASCII value fails or mangles per provider, so both
        // are rejected like Backoff rather than silently coerced.
        if (
            overrides.RunbookUrl is { } url
            && (url.Length > ActaTextLimits.DefinitionRunbookUrl || url.AsSpan().ContainsAnyExceptInRange((char)0x20, (char)0x7E))
        )
        {
            throw new ArgumentException(
                $"RunbookUrl override must be at most {ActaTextLimits.DefinitionRunbookUrl} printable ASCII characters.",
                nameof(overrides)
            );
        }

        // Display name and description are free-form text: truncated to their columns like every other
        // operator-supplied message, so an over-length value never surfaces as a provider write error.
        overrides = overrides with
        {
            DisplayName = overrides.DisplayName.Truncate(ActaTextLimits.DefinitionDisplayName),
            Description = overrides.Description.Truncate(ActaTextLimits.DefinitionDescription),
        };

        var actor = new JobControlActor(ActorCode.Operator, actorKey.Truncate(ActaTextLimits.ActorKey));

        var definitionId = await ResolveDefinitionIdAsync(jobNamespace, jobName, ct);
        if (definitionId is null)
        {
            return new DefinitionControlResult(ControlAction.NotFound);
        }

        var outcome = await store.SetDefinitionOverridesAsync(
            new SetDefinitionOverridesCommand(
                definitionId.Value,
                expectedVersion,
                overrides,
                actor,
                reasonMessage.Truncate(ActaTextLimits.ReasonMessage)
            ),
            ct
        );

        return new DefinitionControlResult(
            outcome.Action switch
            {
                DefinitionOverrideAction.Applied => ControlAction.Applied,
                DefinitionOverrideAction.NotFound => ControlAction.NotFound,
                _ => ControlAction.Rejected,
            }
        );
    }

    /// <summary>
    /// Registers the namespace's whole definitions set: resolves descriptors to rows, then skips the
    /// upsert entirely when nothing is new, changed, or needs retiring - a steady-state restart issues
    /// zero writes and takes no locks. Returns a name-to-id map for every descriptor.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> RegisterAsync(
        int namespaceId,
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

        return result.Count != rows.Count
            ? throw new InvalidOperationException(
                $"register_job_definitions returned {result.Count} name-to-id rows for {rows.Count} input definitions. "
                    + "The routine must return exactly one row per registered definition."
            )
            : (IReadOnlyDictionary<string, int>)result;
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

    /// <summary>
    /// Rejects a descriptor whose backoff, timeout, runbook URL, display name or description exceeds
    /// the bounds the operator override gate enforces, so the two surfaces share one contract.
    /// Declared text is rejected rather than truncated: the attribute is the developer's to fix.
    /// </summary>
    internal static void ValidateDescriptorShape(JobDescriptor descriptor, string namespaceLabel)
    {
        // Hand-authored IJobManifest descriptors bypass the generator's compile-time Backoff check, so
        // this is the last gate before an invalid expression reaches the DB and crash-loops every
        // execution.
        var backoff = descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression;
        if (!Backoff.TryParse(backoff, out _) || backoff.Length > ActaTextLimits.DefinitionBackoff)
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) has an invalid Backoff expression "
                    + $"\"{backoff}\": it must be a valid Acta backoff expression of at most {ActaTextLimits.DefinitionBackoff} characters."
            );
        }

        var executionTimeout = descriptor.ExecutionTimeoutSeconds ?? JobDefinitionRegistration.DefaultExecutionTimeoutSeconds;
        if (executionTimeout > JobDefinitionRegistration.MaxExecutionTimeoutSeconds)
        {
            throw new ArgumentException(
                $"Job definition \"{descriptor.JobName}\" (namespace {namespaceLabel}) declares an ExecutionTimeout of {executionTimeout} seconds: "
                    + $"the ceiling is {JobDefinitionRegistration.MaxExecutionTimeoutSeconds} seconds."
            );
        }
        if (
            descriptor.RunbookUrl is { } runbookUrl
            && (
                runbookUrl.Length > ActaTextLimits.DefinitionRunbookUrl
                || runbookUrl.AsSpan().ContainsAnyExceptInRange((char)0x20, (char)0x7E)
            )
        )
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
    }

    private static JobDefinitionRow BuildRow(JobDescriptor descriptor, int namespaceId)
    {
        var priorityCode = (byte)descriptor.Priority;
        var maxAttempts = descriptor.MaxAttempts;
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
        var contract = ContractOf(descriptor);

        ValidateDescriptorShape(descriptor, namespaceId.ToString(CultureInfo.InvariantCulture));

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
