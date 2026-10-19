using System.Text.Json;

namespace Acta;

/// <summary>
/// Rebuilds the canonical <see cref="EnqueueJobRequest"/> from a claimed <see cref="ClaimOutboxRow"/> using
/// the raw enqueue vocabulary. A null <c>priority_code</c> stays null in the request (no override); the
/// external table's <c>ParentJobId</c>-less contract means the reconstructed request is always a root job.
/// A malformed <c>meta</c>, missing tag name, an over-cap payload, an unsupported <c>input_format_id</c>, or input
/// its format rejects (json or text that is not UTF-8 or holds a NUL byte) throws <see cref="OutboxContractException"/>
/// for immediate quarantine.
/// </summary>
internal static class OutboxRequestReconstruction
{
    public static EnqueueJobRequest ToRequest(ClaimOutboxRow row, int maxInlinePayloadBytes)
    {
        var inputLength = row.Input?.Length ?? 0;
        if (inputLength > maxInlinePayloadBytes)
        {
            throw new OutboxContractException(
                $"payload is {inputLength} bytes, above the target inline cap of {maxInlinePayloadBytes} bytes."
            );
        }

        var tags = OutboxMetaReader.Parse(row.MetaJson);
        var input = JobPayload.None;
        if (row.InputFormatId != 0)
        {
            JobPayloadFormat format;
            try
            {
                // A reserved format id (4..127) is not a real payload format: turn the ForId reject into a
                // deterministic contract error so the row quarantines instead of failing the whole tick.
                format = JobPayloadFormat.ForId(row.InputFormatId);
            }
            catch (ArgumentException ex)
            {
                throw new OutboxContractException($"input_format_id {row.InputFormatId} is not a supported payload format: {ex.Message}");
            }

            try
            {
                input = JobPayload.FromBytes(format, row.Input ?? []);
            }
            catch (ArgumentException ex)
            {
                throw new OutboxContractException($"input does not fit input_format_id {row.InputFormatId}: {ex.Message}");
            }
        }

        return new EnqueueJobRequest(
            JobNamespace: row.JobNamespace,
            JobName: row.JobName,
            Input: input,
            DeduplicationKey: row.DeduplicationKey,
            CorrelationKey: row.CorrelationKey,
            ConcurrencyKey: row.ConcurrencyKey,
            Priority: row.PriorityCode is { } p ? (JobPriorityCode)p : null,
            NextRunAtUtc: row.NextRunAtUtc,
            DelaySeconds: row.DelaySeconds,
            Tags: tags,
            ParentJobId: null,
            TenantName: row.TenantName,
            Lane: row.Lane
        );
    }
}

/// <summary>
/// AOT-safe source-generated parse of the external-outbox <c>meta</c> bag, the mirror of
/// <c>OutboxMetaWriter</c>. Reads the documented <c>{"tags":[{"name":..,"value":..}]}</c> shape from the
/// single shared <see cref="OutboxMetaJson"/>: a root object, an optional ordered <c>tags</c> array where
/// <c>name</c> is a required string and <c>value</c> is a string or JSON null. A malformed shape is a
/// deterministic contract error the relay quarantines.
/// </summary>
internal static class OutboxMetaReader
{
    /// <summary>
    /// The tags declared in <paramref name="metaJson"/>, or null when there is no <c>meta</c>. Throws
    /// <see cref="OutboxContractException"/> when the JSON is present but does not match the shape.
    /// </summary>
    public static IReadOnlyList<TagInput>? Parse(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return null;
        }

        OutboxMetaJson? meta;
        try
        {
            meta = JsonSerializer.Deserialize(metaJson, OutboxMetaJsonContext.Default.OutboxMetaJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxContractException($"meta is not valid JSON: {ex.Message}");
        }

        if (meta?.Tags is not { Count: > 0 } tags)
        {
            return null;
        }

        var result = new List<TagInput>(tags.Count);
        foreach (var tag in tags)
        {
            if (tag is null || string.IsNullOrEmpty(tag.Name))
            {
                throw new OutboxContractException("meta.tags entry is null or missing a non-empty 'name'.");
            }
            result.Add(new TagInput(tag.Name, tag.Value));
        }
        return result;
    }
}
