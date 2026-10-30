using System.Collections.Concurrent;
using System.Text;

namespace Acta;

/// <summary>
/// Resolves a schedule's calendar names into a <see cref="ScheduleSource"/>, afresh at every evaluation: a calendar
/// declared in the process (<see cref="CalendarRegistry"/>) first, then the current family of the namespace's catalog
/// or of the shared one in <c>sys</c>; a name the process declares never reads the catalog. Each catalog version
/// compiles once per process and is kept, since a version's content never changes. The admitter preloads the names of
/// a pass before it plans, so planning stays synchronous; registration, preview, and publication preload as they go.
/// The engine reaches it through <see cref="CalendarsOperations"/>.
/// </summary>
internal sealed class CalendarSources(
    CalendarRegistry registry,
    GetCurrentCalendarsHandler families,
    ResolveCalendarVersionHandler versions
)
{
    private readonly ConcurrentDictionary<(int NamespaceId, string Name, int Version), CompiledCalendar> _compiled = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, int>> _current = new();

    /// <summary>Whether the process declares <paramref name="name"/> under <paramref name="role"/>, which the catalog never overrides.</summary>
    public bool Declares(string name, CalendarRoleCode role) => registry.Declares(name, role);

    /// <summary>
    /// The source of <paramref name="timing"/> from the declared calendars and the current versions the namespace's
    /// preloads read; a name that resolves to nothing is reported when the source is used.
    /// </summary>
    public ScheduleSource Source(int namespaceId, ScheduleTiming timing)
    {
        var source = registry.Source(timing);
        if (source.Calendar is null && timing.ExpressionKind == ScheduleExpressionKindCode.Calendar)
        {
            source = source with { Calendar = Current(namespaceId, timing.Expression)?.Calendar };
        }

        if (source.Availability is null && timing.AvailabilityCalendarName is { } name)
        {
            source = source with { Availability = Current(namespaceId, name)?.Availability };
        }

        return source;
    }

    public async Task<ScheduleSource> SourceAsync(int namespaceId, ScheduleTiming timing, CancellationToken ct)
    {
        await PreloadAsync(namespaceId, timing.CalendarNames(), ct);
        return Source(namespaceId, timing);
    }

    /// <summary>
    /// For the names among <paramref name="names"/> the process does not declare: reads the current families of the
    /// namespace and of <c>sys</c> once, compiles the current version of each this process has not met, and keeps those
    /// versions for <see cref="Source"/>. Only the names asked for move, so a preload never points another caller's
    /// name at a version it did not compile. No names, no read.
    /// </summary>
    public async Task PreloadAsync(int namespaceId, IEnumerable<(string Name, CalendarRoleCode Role)> names, CancellationToken ct)
    {
        var needed = names.Where(n => !registry.Declares(n.Name, n.Role)).Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();
        if (needed.Count == 0)
        {
            return;
        }

        var current = (await families.HandleAsync(new GetCurrentCalendarsQuery(namespaceId), ct)).Calendars.ToDictionary(
            c => c.CalendarName,
            c => c.Version,
            StringComparer.Ordinal
        );
        var known = _current.GetOrAdd(namespaceId, _ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal));
        foreach (var name in needed)
        {
            if (!current.TryGetValue(name, out var version))
            {
                known.TryRemove(name, out _);
                continue;
            }

            if (!_compiled.ContainsKey((namespaceId, name, version)))
            {
                await CompileAsync(namespaceId, name, version, ct);
            }

            known[name] = version;
        }
    }

    private CompiledCalendar? Current(int namespaceId, string name) =>
        _current.TryGetValue(namespaceId, out var current) && current.TryGetValue(name, out var version)
            ? _compiled.GetValueOrDefault((namespaceId, name, version))
            : null;

    private async Task CompileAsync(int namespaceId, string name, int version, CancellationToken ct)
    {
        if (await versions.HandleAsync(new ResolveCalendarVersionQuery(namespaceId, name, version), ct) is not { } row)
        {
            return;
        }

        try
        {
            _compiled[(namespaceId, name, version)] = CalendarDefinition.Compile(row.Role, row.Definition);
        }
        catch (CalendarDefinitionException ex)
        {
            throw new InvalidOperationException(
                $"The catalog calendar '{name}' version {version} does not compile in this build: {ex.Message}",
                ex
            );
        }
    }
}
