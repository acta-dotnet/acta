using System.Reflection;
using Acta.AspNetCore;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// A read answers a bounded set: a <c>List</c> pages through <see cref="IPagedResponse{T}"/>, and a <c>Get</c>,
/// <c>Explain</c>, or <c>Preview</c> answers one thing whose collections are each capped by a named constant. Walks every
/// read a caller can reach, the public facades' responses and the HTTP ones; an engine-only read is no such read. A
/// collection that could grow with the ledger fails here on the day it is added.
/// </summary>
public sealed class ResponseCollectionTests
{
    private static readonly string[] ReadVerbs = ["Get", "List", "Preview", "Explain"];

    /// <summary>Each capped collection on a read response, keyed <c>Type.Member</c>, with the cap that bounds it.</summary>
    private static readonly Dictionary<string, string> BoundedCollections = new(StringComparer.Ordinal)
    {
        // Each branch of the explanation adds at most three moves.
        ["ExplainJobResponse.NextActions"] = "the explanation's branches",
        ["ExplainJobResponse.Steps"] = nameof(ExplainJobHandler.StepLimit),
        ["GetJobDetailResponse.Schedules"] = nameof(GetJobDetailResponse.SchedulePageSize),
        ["GetJobDetailResponse.Workers"] = nameof(GetJobDetailResponse.WorkerPageSize),
        ["GetJobLineageMapResponse.Ancestors"] = nameof(GetJobLineageMapHandler.AncestorLimit),
        ["GetJobLineageMapResponse.Children"] = nameof(GetJobLineageMapHandler.MaxChildLimit),
        ["GetJobLineageMapResponse.Steps"] = nameof(GetJobLineageMapHandler.StepLimit),
        ["PreviewCalendarResponse.OccurrencesUtc"] = nameof(NextOccurrenceCalculator.MaxWalk),
        ["PreviewCalendarResponse.CurrentOccurrencesUtc"] = nameof(NextOccurrenceCalculator.MaxWalk),
        ["PreviewCalendarResponse.Windows"] = nameof(NextOccurrenceCalculator.MaxWalk),
        ["PreviewCalendarResponse.CurrentWindows"] = nameof(NextOccurrenceCalculator.MaxWalk),
        ["PreviewCalendarResponse.Schedules"] = nameof(PreviewCalendarHandler.ScheduleLimit),
        ["PreviewScheduleResponse.NextOccurrencesUtc"] = nameof(NextOccurrenceCalculator.MaxWalk),
    };

    [Fact]
    public void Every_read_response_collection_is_a_page_or_named_capped()
    {
        var roots = typeof(IJobs)
            .Assembly.GetTypes()
            .Concat(typeof(ActaEndpointRouteBuilderExtensions).Assembly.GetTypes())
            .Where(type => type.Assembly != typeof(IJobs).Assembly || type.IsPublic)
            .Where(type =>
                type.Name.EndsWith("Response", StringComparison.Ordinal)
                && ReadVerbs.Any(verb => type.Name.StartsWith(verb, StringComparison.Ordinal))
            )
            .ToList();
        Assert.True(roots.Count > 30, $"only {roots.Count} read responses found; the walk is broken.");

        var seen = new HashSet<Type>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<(string Key, string? Detail)>();
        foreach (var root in roots)
        {
            Walk(root, seen, reached, violations);
        }

        var stale = BoundedCollections
            .Keys.Where(key => !reached.Contains(key))
            .Select(key => (key, (string?)"names no collection a read reaches"));
        ArchitectureAssert.NoViolations(
            violations.Concat(stale),
            "Page the collection as a List of its own, or cap it with a named constant and add it to BoundedCollections with that cap."
        );
    }

    private static void Walk(Type type, HashSet<Type> seen, HashSet<string> reached, List<(string Key, string? Detail)> violations)
    {
        if (!IsActa(type) || !seen.Add(type))
        {
            return;
        }

        var paged = type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPagedResponse<>));
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var element = Element(property.PropertyType);
            if (element is not null)
            {
                var key = $"{type.Name}.{property.Name}";
                reached.Add(key);
                if (!(paged && property.Name == nameof(IPagedResponse<object>.Items)) && !BoundedCollections.ContainsKey(key))
                {
                    violations.Add((key, $"{property.PropertyType.Name} of {element.Name}"));
                }
                Walk(element, seen, reached, violations);
                continue;
            }

            Walk(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, seen, reached, violations);
        }
    }

    // The element type of a collection member; a string, byte[], or payload memory is a value, not a set.
    private static Type? Element(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>))
        {
            return null;
        }
        if (type.IsArray)
        {
            return type.GetElementType();
        }
        return type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) ? type.GetGenericArguments()[^1] : null;
    }

    private static bool IsActa(Type type) => type.Namespace is "Acta" or "Acta.AspNetCore" && !type.IsEnum && !type.IsPrimitive;
}
