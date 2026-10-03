namespace Acta.Runtime;

/// <summary>One setting write as the facade received it, before validation and encoding.</summary>
internal sealed record SetSettingCommand(
    string Name,
    string Value,
    int? ExpectedVersion,
    string? Description,
    string? NamespaceName,
    string? JobName,
    string? ReasonMessage,
    string? ActorKey
);
