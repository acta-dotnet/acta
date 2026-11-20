namespace Acta;

/// <summary>
/// How many alerts the batch deleted, and how many of them were undelivered: an alert aged out before delivery settled
/// is a signal an operator never got.
/// </summary>
internal readonly record struct PurgeExpiredAlertsResponse(int Purged, int Undelivered);
