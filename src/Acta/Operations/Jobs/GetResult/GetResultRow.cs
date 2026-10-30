namespace Acta;

/// <summary>A jobs_state row's result with the job's namespace and name, which a typed read checks the result type against.</summary>
[DbProjection]
internal sealed record GetResultRow(string JobNamespace, string JobName, byte FormatId, ReadOnlyMemory<byte> Data)
{
    public JobPayloadFormat Format => JobPayloadFormat.ForId(FormatId);

    public JobPayload Payload => JobPayload.Trusted(Format, Data.ToArray());
}
