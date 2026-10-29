namespace Acta;

/// <summary>
/// What a calendar catalog verb did: Applied, Rejected when the family's state or the request refuses it, VersionConflict
/// when the version the caller saw is stale, or NotFound when the verb found nothing to act on. Each verb's response says
/// which version it answers.
/// </summary>
public interface ICalendarControlResponse : IControlResponse;
