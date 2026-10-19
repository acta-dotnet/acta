namespace Acta.AspNetCore;

/// <summary>
/// Per-request authorization seam for Acta's mutating control endpoints (job pause/resume/cancel,
/// tenant/namespace suspend, schedule controls, tag writes, and so on). When registered, every control
/// endpoint consults it before its handler runs; a denial short-circuits to 403 and the handler (so the
/// store call and its audit event) never happens. GET reads and <c>/capabilities</c> are unaffected.
///
/// Not registered by default: absent, every control request is allowed exactly as before this seam
/// existed (unchanged back-compat). The host registers its own implementation directly; no
/// <c>AddActa</c>/<c>UseActa</c> call is needed:
/// <code>
/// services.AddSingleton&lt;IActaControlAuthorizer, MyControlAuthorizer&gt;();
/// </code>
///
/// This seam covers the HTTP surface only. The CLI's control verbs (Cancel/Pause/Resume/Restart/Signal)
/// call <c>IJobs</c> directly and are not routed through it: local-process access is the CLI's own trust
/// model and is unchanged.
/// </summary>
public interface IActaControlAuthorizer
{
    ValueTask<ActaControlVerdict> AuthorizeAsync(ActaControlContext context, CancellationToken ct);
}

/// <summary>Authorization outcome for a control request: allowed, or denied with a caller-facing reason.</summary>
public readonly record struct ActaControlVerdict(bool IsAllowed, string? Reason)
{
    public static ActaControlVerdict Allowed { get; } = new(true, null);

    public static ActaControlVerdict Denied(string reason) => new(false, reason);
}
