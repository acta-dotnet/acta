namespace Acta;

/// <summary>A definition the compiler refuses, with every problem it found, each starting with its JSON path.</summary>
internal sealed class CalendarDefinitionException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
