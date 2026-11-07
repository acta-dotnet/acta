namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /definitions/{jobNamespace}/{jobName}/retire</c>: the <c>ExpectedVersion</c> the operator last read, required.</summary>
internal sealed record RetireDefinitionRequest(int? ExpectedVersion = null, string? ReasonMessage = null) : IControlRequest;
