namespace Acta;

/// <summary>
/// One stored definition's identity, change-detection hash, status, contract columns, and effective policy. Read at
/// startup to drive the payload-contract drift check (Warn/Fail) and the registration write gate, and after
/// registration (plus on the reload tick) to overlay effective policy onto the worker's descriptors.
/// </summary>
internal sealed record StoredDefinitionContract(
    string DefinitionName,
    DefinitionContract Contract,
    int DefinitionId,
    string DefinitionHash,
    DefinitionStatusCode Status,
    EffectiveJobPolicy Effective
);
