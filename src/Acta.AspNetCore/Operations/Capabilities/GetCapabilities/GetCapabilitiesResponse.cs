namespace Acta.AspNetCore;

/// <summary>
/// What this host's API allows, so the dashboard offers only the controls it can use: whether controls are mapped, the
/// Acta version, the provider and schema behind it, and the confirmation header a control request carries.
/// </summary>
internal sealed record GetCapabilitiesResponse(
    bool ControlsEnabled,
    string Version,
    string Provider,
    string Schema,
    string ConfirmationHeader
);
