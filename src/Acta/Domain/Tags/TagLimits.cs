namespace Acta;

/// <summary>Public validation limits shared by tag writes and typed tag filters.</summary>
public static class TagLimits
{
    public const int MaxTagsPerTarget = 32;
    public const int MaxFiltersPerQuery = 16;
    public const int MaxNameLength = 128;
    public const int MaxValueLength = 128;
}
