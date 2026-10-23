namespace Acta;

/// <summary>
/// Provider-independent product limits for free-form text validated before a store call. Relational
/// schema tests pin these constants to the corresponding column declarations.
/// </summary>
internal static class ActaTextLimits
{
    public const int Actor = 128;
    public const int EventMessage = 512;
    public const int AlertChannelName = 128;
    public const int AlertDeduplicationKey = 128;
    public const int AlertTitle = 512;
    public const int AlertMessage = 512;
    public const int DefinitionBackoff = 64;
    public const int DefinitionDisplayName = 128;
    public const int DefinitionDescription = 512;
    public const int DefinitionRunbookUrl = 512;
    public const int DefinitionTypeName = 512;
    public const int ScheduleChangeMessage = 512;
    public const int StepFailureMessage = 512;
}
