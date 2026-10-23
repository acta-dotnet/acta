namespace Acta.AspNetCore;

/// <summary>Wire shape of a documented query parameter.</summary>
internal enum QueryParameterKind : byte
{
    String = 1,
    Int = 2,
    Bool = 3,
    Instant = 4,
    Long = 5,
}
