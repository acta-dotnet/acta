namespace Acta.Runtime.Modules.Outbox;

/// <summary>
/// Rebuilds the canonical <see cref="JobEnqueueRequest"/> from a claimed <see cref="OutboxRow"/> using
/// the raw enqueue vocabulary. A null <c>priority_code</c> stays null in the request (no override); the
/// external table's <c>ParentJobId</c>-less contract means the reconstructed request is always a root job.
/// A malformed <c>meta</c>, missing tag name, an over-cap payload, or an unsupported <c>input_format_id</c>
/// throws <see cref="OutboxContractException"/> for immediate quarantine.
/// </summary>
internal static class OutboxRequestReconstruction
{
    public static JobEnqueueRequest ToRequest(OutboxRow row, int maxInlinePayloadBytes)
    {
        var inputLength = row.InputData?.Length ?? 0;
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

            input = JobPayload.FromBytes(format, row.InputData ?? []);
        }

        return new JobEnqueueRequest(
            JobNamespace: row.JobNamespace,
            JobName: row.JobName,
            Input: input,
            DeduplicationKey: row.DeduplicationKey,
            CorrelationKey: row.CorrelationKey,
            ExclusiveKey: row.ExclusiveKey,
            Priority: row.PriorityCode is { } p ? (JobPriorityCode)p : null,
            NextRunAtUtc: row.NextRunAtUtc,
            DelaySeconds: row.DelaySeconds,
            Tags: tags,
            ParentJobId: null,
            TenantKey: row.TenantKey
        );
    }
}
