namespace Acta.AspNetCore.Web;

/// <summary>
/// Minimal read-side capabilities contract the dashboard consumes to show/hide edit UI. Additive.
/// </summary>
internal sealed record CapabilitiesResponse(bool ControlsEnabled, string Version, string Provider, string ConfirmationHeader);
