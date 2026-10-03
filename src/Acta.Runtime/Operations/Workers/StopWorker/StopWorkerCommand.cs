namespace Acta.Runtime;

/// <summary>The worker to stop, with the namespace its stop event is recorded under.</summary>
internal sealed record StopWorkerCommand(int NamespaceId, int WorkerId);
