namespace Acta;

/// <summary>One heartbeat of a worker: the lease window to push its in-flight jobs to, and whether it is draining.</summary>
internal sealed record ExtendWorkerLeasesCommand(int WorkerId, int LeaseTtlSeconds, bool Draining);
