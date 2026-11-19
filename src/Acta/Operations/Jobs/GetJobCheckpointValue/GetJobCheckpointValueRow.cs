namespace Acta;

/// <summary>
/// A checkpoint's stored value with the time of the write that stored it; the read answers no row for a checkpoint that
/// holds none.
/// </summary>
[DbProjection]
internal sealed record GetJobCheckpointValueRow(byte ValueFormatId, DateTime ModifiedAtUtc, byte[] Value)
{
    public JobPayload Payload => JobPayload.Trusted(JobPayloadFormat.ForId(ValueFormatId), Value);
}
