namespace Acta;

/// <summary>
/// What a publication or a preview reads of a family: its Published version and the version asked for, each null when
/// the family has none. Both are one row when the version asked for is the Published one.
/// </summary>
internal sealed record GetCalendarPublicationResponse(FamilyVersion? Current, FamilyVersion? Candidate);
