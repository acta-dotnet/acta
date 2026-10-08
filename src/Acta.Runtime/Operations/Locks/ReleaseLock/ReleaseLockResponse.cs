namespace Acta.Runtime;

/// <summary>Whether the hold was given back; false when its token no longer matches.</summary>
internal readonly record struct ReleaseLockResponse(bool Released);
