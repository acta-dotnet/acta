namespace Acta;

/// <summary>
/// One version of a calendar family in a page: its status, the hash of its content, when it was saved and last written, and when it was
/// published, null for a version that never was.
/// </summary>
/// <param name="Version">The version's number within its family, starting at 1.</param>
/// <param name="Status">The version's status.</param>
/// <param name="ContentHash">A hash of the version's definition.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="PublishedAtUtc">When the version was published, in UTC; null when it has not been.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
public sealed record ListCalendarVersionsItem(
    long Version,
    CalendarStatusCode Status,
    string ContentHash,
    DateTime CreatedAtUtc,
    DateTime? PublishedAtUtc,
    DateTime ModifiedAtUtc
);
