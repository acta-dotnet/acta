using System.Collections.Concurrent;
using System.Text;

namespace Acta;

/// <summary>
/// Resolves a schedule's calendar bindings into a <see cref="ScheduleSource"/>: a Registered binding through the
/// process's <see cref="CalendarRegistry"/>, a Catalog binding through the namespace's catalog or the shared one in
/// <c>sys</c>, each version compiled once per process and kept, since a version's content never changes. The admitter
/// preloads the bindings of a pass before it plans, so planning stays synchronous; registration, preview, and
/// publication resolve as they go. The engine reaches it through <see cref="CalendarsOperations"/>.
/// </summary>
internal sealed class CalendarSources(CalendarRegistry registry, ResolveCalendarVersionHandler versions)
{
    private readonly ConcurrentDictionary<(int NamespaceId, string Name, int Version), CompiledCalendar> _catalog = new();

    /// <summary>The source of <paramref name="timing"/> from what is registered and preloaded; a missing binding is reported when the source is used.</summary>
    public ScheduleSource Source(int namespaceId, ScheduleTiming timing)
    {
        var source = registry.Source(timing);
        if (
            timing is
            { ExpressionKind: ScheduleExpressionKindCode.Calendar, CalendarOrigin: CalendarOriginCode.Catalog, CalendarVersion: { } v }
        )
        {
            source = source with { Calendar = _catalog.GetValueOrDefault((namespaceId, timing.Expression, v))?.Calendar };
        }

        if (timing is { AvailabilityCalendarName: { } name, AvailabilityOrigin: CalendarOriginCode.Catalog, AvailabilityVersion: { } av })
        {
            source = source with { Availability = _catalog.GetValueOrDefault((namespaceId, name, av))?.Availability };
        }

        return source;
    }

    /// <summary>Whether a code calendar of <paramref name="name"/> implements <paramref name="role"/>; code resolves before the catalog.</summary>
    public bool HasCode(string name, CalendarRoleCode role) => registry.Has(name, role);

    public async Task<ScheduleSource> SourceAsync(int namespaceId, ScheduleTiming timing, CancellationToken ct)
    {
        await PreloadAsync(namespaceId, timing.CatalogBindings(), ct);
        return Source(namespaceId, timing);
    }

    /// <summary>Compiles the catalog versions among <paramref name="bindings"/> this process has not met; one absent in the catalog stays unresolved.</summary>
    public async Task PreloadAsync(int namespaceId, IEnumerable<(string Name, int Version)> bindings, CancellationToken ct)
    {
        foreach (var (name, version) in bindings.Distinct())
        {
            if (_catalog.ContainsKey((namespaceId, name, version)))
            {
                continue;
            }

            if (await versions.HandleAsync(new ResolveCalendarVersionQuery(namespaceId, name, version), ct) is not { } row)
            {
                continue;
            }

            try
            {
                _catalog[(namespaceId, name, version)] = CalendarDefinition.Compile(row.Role, Encoding.UTF8.GetString(row.Definition.Span));
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
}
