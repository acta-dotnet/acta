namespace Acta.AspNetCore;

/// <summary>
/// What this host's API offers: whether the controls are enabled, the Acta version, the provider and schema behind
/// it, and the name of the header that confirms a control request. An enabled control still needs its permission.
/// </summary>
/// <param name="ControlsEnabled">Whether this host enables the controls, which answer 403 while it does not; it does not say this caller may use them.</param>
/// <param name="EngineVersion">The version of Acta this host runs.</param>
/// <param name="Provider">The database provider this host runs on.</param>
/// <param name="Schema">The database schema that holds Acta's tables.</param>
/// <param name="ConfirmationHeader">
/// The name of the confirmation header, which a control sends with the value true unless it sends a JSON content type or a
/// bearer token.
/// </param>
/// <param name="Permissions">
/// The permissions this caller holds, of acta.read, acta.enqueue, acta.control, and acta.admin: under RequirePermissions
/// the ones whose policy the caller satisfies, otherwise all four. A control also needs controlsEnabled.
/// </param>
internal sealed record GetCapabilitiesResponse(
    bool ControlsEnabled,
    string EngineVersion,
    string Provider,
    string Schema,
    string ConfirmationHeader,
    IReadOnlyList<string> Permissions
);
