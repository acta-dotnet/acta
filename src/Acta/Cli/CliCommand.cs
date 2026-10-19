namespace Acta;

/// <summary>
/// One parsed CLI command. Target is the raw id-or-deduplication-key token, or null when omitted (the
/// runner then fills it from the clipboard); numeric resolution to a JobLookup happens in the
/// runner, where the registered namespaces are known. Take and Cursor page the events verb.
/// </summary>
internal sealed record CliCommand(
    CliVerb Verb,
    string? Target,
    string? SignalName,
    string? SignalValue,
    string? Reason,
    string? Namespace,
    bool Json,
    int? Take = null,
    string? Cursor = null,
    bool Break = false
);
