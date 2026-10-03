namespace Acta.Runtime;

/// <summary>
/// One stored definition's identity, generation, change-detection hash, status, contract columns, last
/// modification time, and effective policy. Read at startup to drive the payload-contract drift check
/// (Warn/Fail) and the registration write gate (skip the upsert when nothing is new or changed), and
/// after registration (plus on the reload tick) to overlay effective policy onto the worker's descriptors.
/// </summary>
internal sealed record StoredDefinitionContract(
    string Name,
    DateTime ManifestGenerationAtUtc,
    DefinitionContract Contract,
    int Id,
    string DefinitionHash,
    JobDefinitionStatusCode Status,
    DateTime ModifiedAtUtc,
    EffectiveJobPolicy Effective
);
