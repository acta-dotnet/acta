namespace Acta.Tests.Contracts;

/// <summary>
/// How names relate across an operation's homes: the facade method named for an operation, and the operation or group
/// noun a spec starts with. The operations index and the architecture tests read them the same way.
/// </summary>
internal static class OperationNames
{
    // The group noun each facade's methods drop, singular; its plural is tried first.
    private static readonly Dictionary<string, string> Nouns = new(StringComparer.Ordinal)
    {
        ["Jobs"] = "Job",
        ["Schedules"] = "Schedule",
        ["Calendars"] = "Calendar",
        ["Definitions"] = "Definition",
        ["Workers"] = "Worker",
        ["Alerts"] = "Alert",
        ["Tenants"] = "Tenant",
        ["Namespaces"] = "Namespace",
        ["Tags"] = "Tag",
        ["Outbox"] = "Outbox",
    };

    // Dropping the noun would give a sibling's name: EnqueueJobs keeps it beside EnqueueJob's EnqueueAsync.
    private static readonly HashSet<(string Facade, string Operation)> KeepsItsNoun = [("Jobs", "EnqueueJobs")];

    /// <summary>The facade method name for <paramref name="operation"/> on <c>I{facade}</c>, without <c>Async</c>.</summary>
    public static string FacadeMethod(string facade, string operation) =>
        KeepsItsNoun.Contains((facade, operation)) || !Nouns.TryGetValue(facade, out var noun) ? operation
        : WithoutWord(operation, noun + "s") is { } plural ? plural
        : WithoutWord(operation, noun) ?? operation;

    /// <summary>Whether <paramref name="method"/> is the one named for <paramref name="operation"/>, async or not.</summary>
    public static bool IsNamedFor(string facade, string method, string operation) =>
        FacadeMethod(facade, operation) is var expected && (method == expected + "Async" || method == expected);

    /// <summary>
    /// The operation a route folder is named for: its own name, or the name without the owner that qualifies it, so
    /// <c>UpsertTenantTag</c> is <c>UpsertTag</c>; null when it names none of <paramref name="operations"/>.
    /// </summary>
    public static string? RouteOwner(string route, IReadOnlySet<string> operations) =>
        operations.Contains(route)
            ? route
            : Nouns.Values.Select(owner => WithoutWord(route, owner)).FirstOrDefault(name => name is not null && operations.Contains(name));

    /// <summary>
    /// The operation a spec belongs to: the longest of <paramref name="operations"/> its name starts with at a word
    /// boundary, so <c>GetJobIdSpec</c> is <c>GetJobId</c>'s and not <c>GetJob</c>'s; null when none does.
    /// </summary>
    public static string? SpecOwner(string spec, IEnumerable<string> operations) =>
        operations.Where(operation => StartsWithWord(spec, operation)).MaxBy(operation => operation.Length);

    /// <summary>
    /// The noun of <paramref name="group"/> a spec's name starts with at a word boundary: the group's name, or its
    /// singular when a facade drops it, so <c>JobIdentifierShapeSpec</c> is a <c>Jobs</c> spec; null when neither.
    /// </summary>
    public static string? SpecNoun(string spec, string group) =>
        new[] { group, Nouns.GetValueOrDefault(group) }.OfType<string>().FirstOrDefault(noun => StartsWithWord(spec, noun));

    // Whether the text starts with the word and another PascalCase word follows it.
    private static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.Ordinal) && text.Length > word.Length && char.IsUpper(text[word.Length]);

    // The text without the first occurrence of the word as a whole PascalCase word, or null when it has none.
    private static string? WithoutWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var end = at + word.Length;
            if (end == text.Length || char.IsUpper(text[end]))
            {
                return text.Remove(at, word.Length);
            }
        }

        return null;
    }
}
