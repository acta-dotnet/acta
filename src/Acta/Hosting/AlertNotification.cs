namespace Acta;

/// <summary>
/// The fields a transport renders, projected from a <c>alerts</c> row by the delivery loop.
/// <see cref="RunbookUrl"/> comes from the job's definition, resolved at delivery time.
/// </summary>
public sealed record AlertNotification(
    AlertRef AlertRef,
    string JobNamespace,
    JobRef? JobRef,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string? RunbookUrl,
    int OccurrenceCount,
    DateTime CreatedAtUtc
);
