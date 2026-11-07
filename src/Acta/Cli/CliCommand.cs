namespace Acta;

/// <summary>
/// One parsed CLI command. Target is the job token as typed, or null when omitted (the runner then
/// fills a read verb's from the clipboard); resolution to a JobLookup happens in the runner, where the
/// registered namespaces are known. Take and Cursor page the events verb; Force lets deploy and
/// migrate deploy an older build.
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
    bool Break = false,
    bool Force = false
);
