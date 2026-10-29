namespace Acta;

/// <summary>The attempt asking for its firing's envelope, identified by its execution fence.</summary>
internal sealed record GetFiringQuery(ExecutionFence Fence);
