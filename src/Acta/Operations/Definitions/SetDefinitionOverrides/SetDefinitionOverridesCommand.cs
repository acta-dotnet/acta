namespace Acta;

/// <summary>An operator's override set for one definition, guarded by the version they last read.</summary>
internal sealed record SetDefinitionOverridesCommand(
    string JobNamespace,
    string JobName,
    int ExpectedVersion,
    DefinitionPolicyOverrides Overrides,
    string? ReasonMessage,
    string? ActorKey
);
