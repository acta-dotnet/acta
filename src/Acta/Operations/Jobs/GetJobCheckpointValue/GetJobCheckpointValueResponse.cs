namespace Acta;

/// <summary>
/// The value one checkpoint of a job holds, in the format it was stored in, returned by
/// <see cref="IJobs.GetCheckpointValueAsync"/>.
/// </summary>
public sealed record GetJobCheckpointValueResponse(JobPayload Value);
