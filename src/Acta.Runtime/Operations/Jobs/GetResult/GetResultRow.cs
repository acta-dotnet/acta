namespace Acta.Runtime;

/// <summary>One <c>results</c> row; its format and data buffer reconstitute a <see cref="JobPayload"/>.</summary>
[DbProjection]
internal sealed record GetResultRow(int ExecutionNumber, byte FormatId, ReadOnlyMemory<byte> Data, DateTime CreatedAtUtc)
{
    public JobPayloadFormat Format => JobPayloadFormat.ForId(FormatId);
}
