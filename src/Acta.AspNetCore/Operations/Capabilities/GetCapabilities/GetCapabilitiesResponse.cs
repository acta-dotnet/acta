namespace Acta.AspNetCore;

/// <summary>
/// What this host's API allows, so the dashboard offers only the controls it can use: whether controls are mapped, the
/// Acta version, the provider and schema behind it, and the name of the header that confirms a control request.
/// </summary>
/// <param name="ControlsEnabled">Whether this host maps the control routes.</param>
/// <param name="EngineVersion">The version of Acta this host runs.</param>
/// <param name="Provider">The database provider this host runs on.</param>
/// <param name="Schema">The database schema that holds Acta's tables.</param>
/// <param name="ConfirmationHeader">
/// The name of the confirmation header, which a control sends with the value true unless it sends a JSON content type or a
/// bearer token.
/// </param>
internal sealed record GetCapabilitiesResponse(
    bool ControlsEnabled,
    string EngineVersion,
    string Provider,
    string Schema,
    string ConfirmationHeader
);
