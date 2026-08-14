namespace Acta.AspNetCore.Features.Alerts;

/// <summary>
/// Body of an alert acknowledge/resolve POST. Entirely optional: an absent body (or an absent/blank
/// <c>ReasonMessage</c>) is a presence-only request. The framework stamps the actor itself, so the body carries
/// only the operator note.
/// </summary>
internal sealed record AlertControlRequest(string? ReasonMessage = null);
