namespace Acta.Runtime;

/// <summary>The definition an operator retires, guarded by the version they last read.</summary>
internal sealed record RetireJobDefinitionCommand(
    string JobNamespace,
    string JobName,
    int ExpectedVersion,
    string? ReasonMessage,
    string? ActorKey
);
