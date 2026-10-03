namespace Acta;

/// <summary>What <see cref="ISettings.SetAsync"/> did, and the setting's version after the attempt.</summary>
public sealed record SetSettingResponse(ControlAction Action, int? Version) : IControlResponse;
