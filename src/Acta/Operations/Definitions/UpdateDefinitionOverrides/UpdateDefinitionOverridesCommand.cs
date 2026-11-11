namespace Acta;

/// <summary>An operator's override set for one definition, guarded by the version they last read.</summary>
internal sealed record UpdateDefinitionOverridesCommand(
    string JobNamespace,
    string JobName,
    long ExpectedVersion,
    DefinitionPolicyOverrides Overrides,
    string? ReasonMessage,
    string? Actor
);
