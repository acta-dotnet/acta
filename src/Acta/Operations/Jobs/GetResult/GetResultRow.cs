namespace Acta;

/// <summary>A jobs_state row's result; its format and data buffer reconstitute a <see cref="JobPayload"/>.</summary>
[DbProjection]
internal sealed record GetResultRow(byte FormatId, ReadOnlyMemory<byte> Data)
{
    public JobPayloadFormat Format => JobPayloadFormat.ForId(FormatId);
}
