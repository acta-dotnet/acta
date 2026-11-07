using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;

namespace Acta;

/// <summary>
/// Resolves a schedule's calendar names into a <see cref="ScheduleSource"/>, afresh at every evaluation: the current
/// family of the namespace's catalog or of the shared one in <c>sys</c>. Each catalog version compiles once per process
/// and is kept, since a version's content never changes. A preload reads which versions are current and answers a
/// resolver over exactly those, so the admitter plans a pass synchronously from its own read, and no other caller's
/// slower read can point it at an older version. The engine reaches it through <see cref="CalendarsOperations"/>.
/// </summary>
internal sealed class CalendarSources(ListCurrentCalendarsHandler families, GetCalendarVersionHandler versions)
{
    private readonly ConcurrentDictionary<(int NamespaceId, string Name, int Version), CompiledCalendar> _compiled = new();

    // A published version this build cannot compile, with the compiler's problems: kept so the schedule that names it
    // says so, and no other schedule of the pass is held up.
    private readonly ConcurrentDictionary<(int NamespaceId, string Name, int Version), string> _unreadable = new();

    public async Task<ScheduleSource> SourceAsync(int namespaceId, ScheduleTiming timing, CancellationToken ct) =>
        (await PreloadAsync(namespaceId, timing.CalendarNames(), ct))(timing);

    /// <summary>
    /// Reads the current families of the namespace and of <c>sys</c> once, compiles the current version of each of
    /// <paramref name="names"/> this process has not met, and answers a resolver over the versions this read found. A
    /// name the read did not cover, or that has no published version, resolves to nothing, which is reported when the
    /// source is used. No names, no read.
    /// </summary>
    public Task<Func<ScheduleTiming, ScheduleSource>> PreloadAsync(int namespaceId, IEnumerable<string> names, CancellationToken ct) =>
        PreloadAsync(namespaceId, names, transaction: null, ct);

    /// <summary>The same preload, reading inside <paramref name="transaction"/> when a deploy owns one, on its connection.</summary>
    public async Task<Func<ScheduleTiming, ScheduleSource>> PreloadAsync(
        int namespaceId,
        IEnumerable<string> names,
        DbTransaction? transaction,
        CancellationToken ct
    )
    {
        var read = new Dictionary<string, int>(StringComparer.Ordinal);
        var needed = names.Distinct(StringComparer.Ordinal).ToList();
        if (needed.Count > 0)
        {
            var current = (await families.HandleAsync(new ListCurrentCalendarsQuery(namespaceId, transaction), ct)).Calendars.ToDictionary(
                c => c.CalendarName,
                c => c.Version,
                StringComparer.Ordinal
            );
            foreach (var name in needed)
            {
                if (!current.TryGetValue(name, out var version))
                {
                    continue;
                }

                if (!_compiled.ContainsKey((namespaceId, name, version)) && !_unreadable.ContainsKey((namespaceId, name, version)))
                {
                    await CompileAsync(namespaceId, name, version, transaction, ct);
                }

                read[name] = version;
            }
        }

        return timing => Source(namespaceId, read, timing);
    }

    private ScheduleSource Source(int namespaceId, Dictionary<string, int> read, ScheduleTiming timing)
    {
        var occurrence =
            timing.ExpressionKind == ScheduleExpressionKindCode.Calendar
                ? Resolve(namespaceId, read, timing.Expression, CalendarRoleCode.Occurrence)
                : default;
        var availability = timing.AvailabilityCalendarName is { } name
            ? Resolve(namespaceId, read, name, CalendarRoleCode.Availability)
            : default;
        return new(timing, occurrence.Calendar?.Calendar, availability.Calendar?.Availability)
        {
            CalendarProblem = occurrence.Problem,
            AvailabilityProblem = availability.Problem,
        };
    }

    // The name's version in the read, in the role asked for, or why there is none to use; neither when no version is published.
    private (CompiledCalendar? Calendar, string? Problem) Resolve(
        int namespaceId,
        Dictionary<string, int> read,
        string name,
        CalendarRoleCode role
    )
    {
        if (!read.TryGetValue(name, out var version))
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

    private async Task CompileAsync(int namespaceId, string name, int version, DbTransaction? transaction, CancellationToken ct)
    {
        if (await versions.HandleAsync(new GetCalendarVersionQuery(namespaceId, name, version, transaction), ct) is not { } row)
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
