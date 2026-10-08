namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /definitions/{jobNamespace}/{jobName}/retire</c>: the <c>ExpectedVersion</c> the operator last read.</summary>
internal sealed record RetireDefinitionRequest(int ExpectedVersion = 0, string? ReasonMessage = null) : IControlRequest;
