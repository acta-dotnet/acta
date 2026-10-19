namespace Acta;

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
