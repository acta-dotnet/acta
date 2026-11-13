namespace Acta;

/// <summary>
/// A job's stored input with the job's namespace and name, which a typed read checks the input type against. The input
/// buffer is NULL for a no-input job.
/// </summary>
[DbProjection]
internal sealed record GetJobInputRow(string JobNamespace, string JobName, byte FormatId, byte[]? Input)
{
    public JobPayloadFormat Format => JobPayloadFormat.ForId(FormatId);

    public JobPayload Payload => JobPayload.Trusted(Format, Input ?? []);
}
