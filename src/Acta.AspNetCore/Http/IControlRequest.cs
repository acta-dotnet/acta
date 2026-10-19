namespace Acta.AspNetCore;

/// <summary>
/// The body of a control verb; every one but purge may carry the operator's reason. The framework stamps the actor and
/// reason code itself, so callers cannot forge the audit trail; an absent optional body acts with no reason.
/// </summary>
internal interface IControlRequest
{
    string? ReasonMessage { get; }
}
