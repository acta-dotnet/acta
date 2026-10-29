namespace Acta;

/// <summary>
/// One version of a family in <see cref="GetCalendarResponse.Versions"/>: its status, the hash of its content, when it was
/// saved and last written, and when it was published, null for a version that never was.
/// </summary>
public sealed record GetCalendarVersion(
    int Version,
    CalendarStatusCode Status,
    string ContentHash,
    DateTime CreatedAtUtc,
    DateTime? PublishedAtUtc,
    DateTime ModifiedAtUtc
);
