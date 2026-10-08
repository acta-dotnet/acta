namespace Acta.Runtime;

/// <summary>What the start CAS did: started the attempt, or why it could not.</summary>
internal readonly record struct StartExecutionResponse(StartExecutionAction Action);
