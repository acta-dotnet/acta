namespace Acta.Runtime;

/// <summary>An operator's override set for one definition, guarded by the version they last read.</summary>
internal sealed record SetJobDefinitionOverridesCommand(
    string JobNamespace,
    string JobName,
    int ExpectedVersion,
    JobDefinitionPolicyOverrides Overrides,
    string? ReasonMessage,
    string? ActorKey
);
