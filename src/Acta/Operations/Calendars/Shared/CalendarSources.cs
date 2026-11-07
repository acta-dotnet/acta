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

    /// <summary>
    /// The source of <paramref name="timing"/> from the current versions the namespace's preloads read; a name that
    /// resolves to nothing is reported when the source is used.
    /// </summary>
    public ScheduleSource Source(int namespaceId, ScheduleTiming timing) =>
        new(
            timing,
            timing.ExpressionKind == ScheduleExpressionKindCode.Calendar ? Current(namespaceId, timing.Expression)?.Calendar : null,
            timing.AvailabilityCalendarName is { } name ? Current(namespaceId, name)?.Availability : null
        );

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
            throw new InvalidOperationException(
                $"The catalog calendar '{name}' version {version} does not compile in this build: {ex.Message}",
                ex
            );
        }
    }
}
