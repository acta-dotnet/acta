namespace Acta.Runtime;

/// <summary>The (action, status_code, version) row the versioned job control routines answer.</summary>
[DbProjection]
internal sealed record JobControlRow(ControlAction Action, JobStatusCode? Status, int? Version);

/// <summary>The (action, status_code) row of the routines that carry no version: purge, input amend, and signal raise.</summary>
[DbProjection]
internal sealed record JobControlActionRow(ControlAction Action, JobStatusCode? Status);
