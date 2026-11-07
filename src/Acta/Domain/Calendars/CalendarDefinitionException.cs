namespace Acta;

/// <summary>
/// A calendar definition that does not compile. <see cref="ICalendars.SaveDraftAsync"/> throws it before writing anything, and a
/// preview or a publication throws it for a stored version this build cannot read. The message lists every problem on its own
/// line, each starting with its JSON path, as <see cref="Problems"/> does.
/// </summary>
public sealed class CalendarDefinitionException : Exception
{
    internal CalendarDefinitionException(IReadOnlyList<string> problems)
        : base($"The calendar definition does not compile:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}") =>
        Problems = problems;

    /// <summary>Every problem the compiler found, one sentence each, starting with its JSON path: <c>$.weekly[0].days[0]: unknown day.</c></summary>
    public IReadOnlyList<string> Problems { get; }
}
