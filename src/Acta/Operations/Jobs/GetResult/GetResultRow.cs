namespace Acta;

/// <summary>A runtime row's result; its format and data buffer reconstitute a <see cref="JobPayload"/>.</summary>
[DbProjection]
internal sealed record GetResultRow(byte FormatId, ReadOnlyMemory<byte> Data)
{
    public JobPayloadFormat Format => JobPayloadFormat.ForId(FormatId);
}
