namespace Acta;

/// <summary>
/// Captured at UseActa time when the process was started in CLI mode: the args after the
/// reserved word, the namespaces declared via Run and Reference (for deduplication-key defaulting), and the
/// workers declared via Run, whose catalogs the deploy and migrate verbs write.
/// </summary>
internal sealed record CliInvocation(
    IReadOnlyList<string> Args,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<WorkerRegistration> Workers
);
