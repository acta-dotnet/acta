namespace Acta;

/// <summary>Per-tick relay configuration resolved from the source registration and worker options.</summary>
internal sealed record OutboxRelayTickOptions(string SourceName, int QuarantineThreshold, int LeaseTtlSeconds, int MaxInlinePayloadBytes);
