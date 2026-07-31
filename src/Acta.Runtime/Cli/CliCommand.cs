namespace Acta.Runtime.Cli;

/// <summary>
/// The CLI verbs. Read verbs and control verbs take an id-or-deduplication-key target; Debug runs an
/// existing job in this process; Help prints usage.
/// </summary>
internal enum CliVerb : byte
{
    Help = 0,
    Info = 1,
    Status = 2,
    Result = 3,
    Cancel = 4,
    Pause = 5,
    Resume = 6,
    Restart = 7,
    Signal = 8,
    Debug = 9,
    Events = 10,
    Explain = 11,
}

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

/// <summary>
/// Captured at UseActa time when the process was started in CLI mode: the args after the
/// reserved word and the namespaces declared via Run and Reference (for deduplication-key defaulting).
/// </summary>
internal sealed record CliInvocation(IReadOnlyList<string> Args, IReadOnlyList<string> Namespaces);
