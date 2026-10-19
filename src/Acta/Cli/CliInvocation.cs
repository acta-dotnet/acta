namespace Acta;

/// <summary>
/// Captured at UseActa time when the process was started in CLI mode: the args after the
/// reserved word and the namespaces declared via Run and Reference (for deduplication-key defaulting).
/// </summary>
internal sealed record CliInvocation(IReadOnlyList<string> Args, IReadOnlyList<string> Namespaces);
