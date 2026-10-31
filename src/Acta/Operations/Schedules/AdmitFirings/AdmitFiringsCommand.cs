namespace Acta;

/// <summary>
/// One admission pass's plan for one namespace: a group per slot or per per-occurrence schedule (admit a firing, or
/// only move cursors) and a row per schedule the group read, linked to it by ordinal. Each carries the versions it was
/// planned against; a group applies whole or not at all. Decision event details are built here, so the SQL stores them
/// without formatting an instant.
/// </summary>
internal sealed record AdmitFiringsCommand(
    int NamespaceId,
    IReadOnlyList<AdmitFiringsGroup> Groups,
    IReadOnlyList<AdmitFiringsValues> Rows
);

/// <summary>
/// One group of a plan. A slot group names the slot and the version it read. A per-occurrence group names its schedule
/// and the state version its capacity was read under, and when it admits, the root the firing becomes: a ref minted
/// here, the key no occurrence is admitted under twice, and for a manual firing the operator's note and key.
/// </summary>
internal sealed record AdmitFiringsGroup(
    int Ordinal,
    long? JobId,
    int? ExpectedJobVersion,
    int? ScheduleId,
    int? ExpectedStateVersion,
    bool Admit,
    string? Envelope,
    Guid? JobRef = null,
    string? DeduplicationKey = null,
    string? TriggerMessage = null,
    string? TriggerDetails = null
);

/// <summary>
/// One schedule a group read, at its state version and the declaration version its capacity and policies came from: its
/// new cursor fields, the audit each decision leaves, and whether an expired occurrence pauses it for an operator. A
/// check-only row writes nothing; it fails its group when either version moved.
/// </summary>
internal sealed record AdmitFiringsValues(
    int GroupOrdinal,
    int ScheduleId,
    string ScheduleName,
    int ExpectedVersion,
    int ExpectedScheduleVersion,
    DateTime? NextOccurrenceAtUtc,
    DateTime? NextEvaluationAtUtc,
    EvaluationStatusCode EvaluationStatus,
    bool PauseEnded,
    string? AdmittedDetails,
    string? SkippedDetails,
    string? ExpiredDetails,
    bool ExpirePaused,
    bool CheckOnly = false
)
{
    /// <summary>A schedule the group read and leaves as it is, so the group still applies only at what it read.</summary>
    public static AdmitFiringsValues Check(int groupOrdinal, int scheduleId, string scheduleName, int stateVersion, int scheduleVersion) =>
        new(
            groupOrdinal,
            scheduleId,
            scheduleName,
            stateVersion,
            scheduleVersion,
            null,
            null,
            default,
            false,
            null,
            null,
            null,
            false,
            true
        );
}
