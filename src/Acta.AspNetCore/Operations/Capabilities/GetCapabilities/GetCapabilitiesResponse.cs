namespace Acta.AspNetCore;

/// <summary>
/// What this host's API allows, so the dashboard offers only the controls it can use: whether controls are mapped, the
/// Acta version, the provider and schema behind it, and the confirmation header a control request carries.
/// </summary>
/// <param name="ControlsEnabled">Whether this host maps the control routes.</param>
/// <param name="EngineVersion">The version of Acta this host runs.</param>
/// <param name="Provider">The database provider this host runs on.</param>
/// <param name="Schema">The database schema that holds Acta's tables.</param>
/// <param name="ConfirmationHeader">The name of the header a control must carry with the value true.</param>
internal sealed record GetCapabilitiesResponse(
    bool ControlsEnabled,
    string EngineVersion,
    string Provider,
    string Schema,
    string ConfirmationHeader
);
