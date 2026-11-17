namespace Acta;

/// <summary>
/// One parsed CLI command. Target is the job token as typed, or null when omitted (the runner then
/// fills a read verb's from the clipboard); resolution to a JobLookup happens in the runner, where the
/// registered namespaces are known. Take and Cursor page the events verb; Force lets deploy and
/// migrate deploy an older build. Actor is a control's --actor, or null when omitted (the runner then
/// records the operating system's user name).
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
    bool Force = false,
    string? Actor = null
)
{
    /// <summary>Whether the verb only reads the ledger: nothing it runs writes, so its database can be opened read-only.</summary>
    public bool IsRead => Verb is CliVerb.Info or CliVerb.Status or CliVerb.Result or CliVerb.Events or CliVerb.Explain;
}
