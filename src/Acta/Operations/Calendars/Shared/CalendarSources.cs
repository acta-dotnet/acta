using System.Collections.Concurrent;
using System.Text;

namespace Acta;

/// <summary>
/// Resolves a schedule's calendar names into a <see cref="ScheduleSource"/>, afresh at every evaluation: the current
/// family of the namespace's catalog or of the shared one in <c>sys</c>. Each catalog version compiles once per process
/// and is kept, since a version's content never changes. The admitter preloads the names of a pass before it plans, so
/// planning stays synchronous; registration, preview, and publication preload as they go. The engine reaches it through
/// <see cref="CalendarsOperations"/>.
/// </summary>
internal sealed class CalendarSources(ListCurrentCalendarsHandler families, GetCalendarVersionHandler versions)
{
    private readonly ConcurrentDictionary<(int NamespaceId, string Name, int Version), CompiledCalendar> _compiled = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, int>> _current = new();

    // A published version this build cannot compile, with the compiler's problems: kept so the schedule that names it
    // says so, and no other schedule of the pass is held up.
    private readonly ConcurrentDictionary<(int NamespaceId, string Name, int Version), string> _unreadable = new();

    /// <summary>
    /// The source of <paramref name="timing"/> from the current versions the namespace's preloads read; a name that
    /// resolves to nothing is reported when the source is used.
    /// </summary>
    public ScheduleSource Source(int namespaceId, ScheduleTiming timing)
    {
        var occurrence =
            timing.ExpressionKind == ScheduleExpressionKindCode.Calendar
                ? Resolve(namespaceId, timing.Expression, CalendarRoleCode.Occurrence)
                : default;
        var availability = timing.AvailabilityCalendarName is { } name
            ? Resolve(namespaceId, name, CalendarRoleCode.Availability)
            : default;
        return new(timing, occurrence.Calendar?.Calendar, availability.Calendar?.Availability)
        {
            CalendarProblem = occurrence.Problem,
            AvailabilityProblem = availability.Problem,
        };
    }

    public async Task<ScheduleSource> SourceAsync(int namespaceId, ScheduleTiming timing, CancellationToken ct)
    {
        await PreloadAsync(namespaceId, timing.CalendarNames(), ct);
        return Source(namespaceId, timing);
    }

    /// <summary>
    /// Reads the current families of the namespace and of <c>sys</c> once, compiles the current version of each of
    /// <paramref name="names"/> this process has not met, and keeps those versions for <see cref="Source"/>. Only the
    /// names asked for move, so a preload never points another caller's name at a version it did not compile. No names,
    /// no read.
    /// </summary>
    public async Task PreloadAsync(int namespaceId, IEnumerable<string> names, CancellationToken ct)
    {
        var needed = names.Distinct(StringComparer.Ordinal).ToList();
        if (needed.Count == 0)
        {
            return;
        }

        var current = (await families.HandleAsync(new ListCurrentCalendarsQuery(namespaceId), ct)).Calendars.ToDictionary(
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

            if (!_compiled.ContainsKey((namespaceId, name, version)) && !_unreadable.ContainsKey((namespaceId, name, version)))
            {
                await CompileAsync(namespaceId, name, version, ct);
            }

            known[name] = version;
        }
    }

    // The name's current version in the role asked for, or why there is none to use; neither when no version is published.
    private (CompiledCalendar? Calendar, string? Problem) Resolve(int namespaceId, string name, CalendarRoleCode role)
    {
        if (!_current.TryGetValue(namespaceId, out var current) || !current.TryGetValue(name, out var version))
        {
            return default;
        }

        if (_unreadable.TryGetValue((namespaceId, name, version), out var problems))
        {
            return (null, $"The {role.Code} calendar '{name}' is published as version {version}, which this build cannot read: {problems}");
        }

        if (!_compiled.TryGetValue((namespaceId, name, version), out var compiled))
        {
            return default;
        }

        return compiled.Role == role
            ? (compiled, null)
            : (
                null,
                $"The calendar '{name}' is published as an {compiled.Role.Code} calendar, and the schedule names it as its {role.Code} calendar."
            );
    }

    private async Task CompileAsync(int namespaceId, string name, int version, CancellationToken ct)
    {
        if (await versions.HandleAsync(new GetCalendarVersionQuery(namespaceId, name, version), ct) is not { } row)
        {
            return;
        }

        try
        {
            _compiled[(namespaceId, name, version)] = CalendarDefinition.Compile(row.Role, row.Definition);
        }
        catch (CalendarDefinitionException ex)
        {
            _unreadable[(namespaceId, name, version)] = ex.Message;
        }
    }
}
