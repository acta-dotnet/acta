namespace Acta;

/// <summary>
/// The resolved channel a notification is delivered to: its transport kind, endpoint, and opaque
/// per-transport config bytes (decode with <see cref="ConfigFormatId"/> via the payload serializer registry).
/// </summary>
public sealed record AlertTarget(
    string ChannelName,
    string TransportKind,
    string Endpoint,
    byte ConfigFormatId,
    ReadOnlyMemory<byte> Config
);
