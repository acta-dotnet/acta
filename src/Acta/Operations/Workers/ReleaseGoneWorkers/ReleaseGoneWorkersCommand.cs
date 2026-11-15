namespace Acta;

/// <summary>The workers whose process is gone, by id.</summary>
internal sealed record ReleaseGoneWorkersCommand(int[] WorkerIds);
