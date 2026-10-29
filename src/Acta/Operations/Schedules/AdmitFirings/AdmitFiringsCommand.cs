namespace Acta;

/// <summary>
/// One admission pass's plan for one namespace: a group per slot (admit a firing or only move its members' cursors)
/// and a row per evaluated member. Each carries the version it was planned against; a group applies whole or not at
/// all. Decision event details are built here, so the SQL stores them without formatting an instant.
/// </summary>
internal sealed record AdmitFiringsCommand(
    int NamespaceId,
    IReadOnlyList<AdmitFiringsGroup> Groups,
    IReadOnlyList<AdmitFiringsValues> Rows
);

/// <summary>One slot of a plan: whether a firing is admitted, and the envelope JSON it is admitted with.</summary>
internal sealed record AdmitFiringsGroup(long JobId, int ExpectedJobVersion, bool Admit, string? Envelope);

/// <summary>One member schedule of a plan: its new cursor fields and the audit each decision leaves.</summary>
internal sealed record AdmitFiringsValues(
    int ScheduleId,
    long JobId,
    string ScheduleName,
    int ExpectedVersion,
    DateTime? NextOccurrenceAtUtc,
    DateTime? DecidedThroughAtUtc,
    DateTime? NextEvaluationAtUtc,
    EvaluationStatusCode EvaluationStatus,
    bool PauseEnded,
    string? AdmittedDetails,
    string? SkippedDetails
);
