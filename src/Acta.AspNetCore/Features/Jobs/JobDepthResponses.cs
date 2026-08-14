using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.AspNetCore.Features.Jobs;

/// <summary>
/// Format-dispatched HTTP projection of a <see cref="JobPayload"/>: the frontend dispatches on
/// <see cref="Format"/> (the payload format's wire name) and reads whichever body field is present.
/// A <c>none</c> format carries no body field; built-in formats surface as raw JSON, decoded text, or
/// base64 (the fallback for bytes and consumer-defined formats). <see cref="FormatId"/> is always
/// emitted (0 for none) so a clone can round-trip a custom format whose name alone cannot. Acta
/// operators read everything, so payload disclosure is never gated; the one read concern is size. A
/// payload longer than <c>JobsOptions.MaxInlinePayloadBytes</c> is projected as its format identity plus
/// <see cref="ByteLength"/> and <see cref="Truncated"/> with no body field, so the read never ships an
/// outsized blob (a handler result may have been warned-and-persisted past the cap).
/// </summary>
internal sealed record JobPayloadResponse(
    string Format,
    byte FormatId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Json = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ByteLength = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null
)
{
    public static JobPayloadResponse From(JobPayload payload, int maxInlineBytes)
    {
        if (payload.IsNone)
        {
            return new JobPayloadResponse(JobPayloadFormat.NoneName, 0);
        }

        var format = payload.Format;

        // Size is the only payload-read guard: past the inline cap the body is withheld and only the
        // format identity and byte length travel, so an oversized row is never materialized to the wire.
        if (payload.Data.Length > maxInlineBytes)
        {
            return new JobPayloadResponse(format.Name, format.Id, ByteLength: payload.Data.Length, Truncated: true);
        }

        if (format.Id == JobPayloadFormat.Json.Id)
        {
            // Parse (not reflect) so the stored JSON re-emits verbatim without a reflection-based serializer.
            using var doc = JsonDocument.Parse(payload.Data);
            return new JobPayloadResponse(format.Name, format.Id, Json: doc.RootElement.Clone());
        }

        return format.Id == JobPayloadFormat.Text.Id
            ? new JobPayloadResponse(format.Name, format.Id, Text: Encoding.UTF8.GetString(payload.Data.Span))
            : new JobPayloadResponse(format.Name, format.Id, Base64: Convert.ToBase64String(payload.Data.Span));
    }
}

/// <summary>
/// The whole job screen composed into one response so a lightweight job renders from a single request
/// (its only unbounded part, the event history, keeps its own paged endpoint). Built from the existing
/// <see cref="IJobs"/> reads after one <c>ResolveJobIdAsync</c>: the snapshot (the <c>GET /jobs/{ref}</c>
/// shape), the size-capped input/result/checkpoint payloads, the explain and lineage projections, the
/// schedules bound to its slot, and the eligible workers (only while the job is claimable). An absent
/// result, empty schedule set, or empty worker set is a null/empty field, not an error. The two
/// related collections are capped pages, so each ships its filter-wide count alongside.
/// </summary>
internal sealed record JobDetailResponse(
    JobSnapshot Snapshot,
    JobPayloadResponse Input,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobPayloadResponse? Result,
    IReadOnlyList<JobCheckpointResponse> Checkpoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobExplanation? Explain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobLineageMap? Lineage,
    IReadOnlyList<ScheduleListItem> Schedules,
    // Filter-wide count, so the frontend can tell a complete set from the first page of a larger one.
    long? SchedulesTotal,
    // Echo of the snapshot's tenant key at the top level so the summary link needs no snapshot dig.
    // Absent when the job has no tenant.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantKey,
    // Effective retry budget from the definition so the summary can render "n of m consecutive
    // failures" without a second read. Absent when the definition row is gone.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] short? MaxAttemptsEffective,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorkerListItem>? Workers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WorkersTotal
)
{
    // The dashboard walked the same child cap; keep it here so the lineage panel is unchanged.
    private const int ChildLimit = 100;

    /// <summary>
    /// Compose the detail from the individual reads. The reads are independent but cheap and awaited in
    /// sequence (v1); every read is addressed by the already-resolved id so the whole screen costs one
    /// ResolveJobIdAsync. Eligible workers are fetched only while the job is claimable, matching the
    /// dashboard's own <c>status === 'ready'</c> gate.
    /// </summary>
    public static async Task<JobDetailResponse> ComposeAsync(
        IJobs jobs,
        IActaOperations operations,
        JobSnapshot snapshot,
        int maxInlineBytes,
        CancellationToken ct
    )
    {
        var byId = JobLookup.ById(snapshot.JobId);

        var input = await jobs.GetInputAsync(byId, ct);
        var result = await jobs.GetResultAsync(byId, ct);
        var checkpoints = await jobs.GetCheckpointsAsync(byId, ct);
        var explain = await jobs.ExplainAsync(byId, ct);
        var lineage = await jobs.GetLineageMapAsync(byId, new JobLineageMapOptions(ChildLimit), ct);
        var schedules = await operations.Schedules.ListAsync(
            new ListSchedulesQuery(
                JobNamespace: snapshot.JobNamespace,
                JobName: snapshot.JobName,
                LiveOnly: false,
                PageSize: 100,
                IncludeTotal: true
            ),
            ct
        );
        var definition = await operations.Definitions.GetAsync(snapshot.JobDefinitionId, ct);
        // Every worker in the namespace, not just the live ones: the "why isn't this running?" panel
        // needs the whole set to tell "no workers at all" from "workers, none of them active".
        var workers =
            snapshot.Status == JobStatusCode.Ready
                ? await operations.Workers.ListAsync(
                    new ListWorkersQuery(JobNamespace: snapshot.JobNamespace, PageSize: 50, IncludeTotal: true),
                    ct
                )
                : null;

        return new JobDetailResponse(
            snapshot,
            JobPayloadResponse.From(input ?? JobPayload.None, maxInlineBytes),
            result is { } produced ? JobPayloadResponse.From(produced, maxInlineBytes) : null,
            checkpoints.Select(item => JobCheckpointResponse.From(item, maxInlineBytes)).ToList(),
            explain,
            lineage,
            schedules.Items,
            schedules.TotalCount,
            snapshot.TenantKey,
            definition?.MaxAttemptsEffective,
            workers?.Items,
            workers?.TotalCount
        );
    }
}

/// <summary>HTTP projection of one <see cref="JobCheckpointItem"/>; the value carries the format-dispatched shape.</summary>
internal sealed record JobCheckpointResponse(
    JobCheckpointKindCode Kind,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobCheckpointStatusCode? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? DueAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobPayloadResponse? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public static JobCheckpointResponse From(JobCheckpointItem item, int maxInlineBytes) =>
        new(
            item.Kind,
            item.Name,
            item.Status,
            item.DueAtUtc,
            item.Value is { } value ? JobPayloadResponse.From(value, maxInlineBytes) : null,
            item.CreatedAtUtc,
            item.ModifiedAtUtc
        );
}

/// <summary>
/// Body of a POST /jobs enqueue. At most one of <c>Input</c> (raw JSON stored as json), <c>Text</c>
/// (stored as text), or <c>Base64</c> (stored as the binary format named by <c>FormatId</c>, which must
/// be 2 or 128..255) supplies the input; all absent enqueues a no-input job. <c>Priority</c> accepts the
/// code string (e.g. "high").
/// </summary>
internal sealed record JobEnqueueApiRequest(
    string? JobNamespace = null,
    string? JobName = null,
    JsonElement Input = default,
    string? Text = null,
    string? Base64 = null,
    byte? FormatId = null,
    string? DeduplicationKey = null,
    string? CorrelationKey = null,
    string? TenantKey = null,
    JobPriorityCode? Priority = null,
    int? DelaySeconds = null,
    DateTime? NextRunAtUtc = null
);

/// <summary>HTTP projection of a <see cref="JobEnqueueOutcome"/>: the public ref and the coarse action.</summary>
internal sealed record JobEnqueueResponse(JobRef JobRef, JobEnqueueAction Action);

/// <summary>
/// HTTP projection of a <see cref="JobInputTemplate"/>. <c>Template</c> is the skeleton inlined as raw
/// JSON (never a string); a job this host does not know reports every field null with format "none".
/// </summary>
internal sealed record JobInputTemplateResponse(
    string JobNamespace,
    string JobName,
    string? InputTypeName,
    string Format,
    JsonElement? Template
);
