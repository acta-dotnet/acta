namespace Acta;

/// <summary>The envelope of the firing the attempt holds, and whether the attempt is the firing's first.</summary>
internal sealed record GetFiringResponse(FiringEnvelope Firing, bool FirstAttempt);
