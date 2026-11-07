namespace Acta;

/// <summary>
/// The CLI verbs. Read verbs and control verbs take a job ref or deduplication-key target, and a read verb also an
/// id:&lt;n&gt;; Debug runs an existing job in this process; Deploy writes this build's definitions and schedules; Migrate
/// applies the schema and then deploys; neither takes a target; Help prints usage.
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
    Migrate = 12,
    Deploy = 13,
}
