namespace Acta;

/// <summary>The pending command parked under one inbox name.</summary>
internal sealed record GetOutboxSignalResponse(CheckpointValue Signal);
