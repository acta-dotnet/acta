using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;
using static Acta.Generators.JobSignatureReader;

namespace Acta.Generators;

/// <summary>Reads the <c>[Calendar]</c> classes and checks each declaration.</summary>
internal static class CalendarReader
{
    /// <summary>A <c>[Calendar]</c> class and the calendar interfaces it implements; Emit checks the declaration (ACTA0128).</summary>
    internal static DiscoveredCalendar? TransformCalendar(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } cls)
        {
            return null;
        }

        var attr = ctx.Attributes.FirstOrDefault();
        if (attr is null || attr.ConstructorArguments.Length != 1)
        {
            return null;
        }

        return new DiscoveredCalendar(
            CalendarName: attr.ConstructorArguments[0].Value as string ?? "",
            ImplementationType: cls,
            Occurrence: cls.AllInterfaces.Any(i => TypeFullName(i) == ActaCalendarMetadataName),
            Availability: cls.AllInterfaces.Any(i => TypeFullName(i) == ActaAvailabilityCalendarMetadataName),
            Location: cls.Locations.FirstOrDefault() ?? Location.None
        );
    }

    /// <summary>ACTA0128: declaration validity; returns the whole declarations ordered by name.</summary>
    internal static DiscoveredCalendar[] ValidateCalendars(SourceProductionContext spc, ImmutableArray<DiscoveredCalendar> calendars)
    {
        if (calendars.IsDefaultOrEmpty)
        {
            return [];
        }

        var duplicateNames = new HashSet<string>(
            calendars.GroupBy(c => c.CalendarName, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key),
            StringComparer.Ordinal
        );

        var whole = new List<DiscoveredCalendar>();
        foreach (var calendar in calendars)
        {
            var ok = true;
            if (calendar.ImplementationType.IsAbstract)
            {
                ReportDiagnostic(spc, ManifestDiagnostics.AbstractCalendar(calendar));
                ok = false;
            }
            if (!IsNameable(calendar.ImplementationType))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.UnnameableCalendar(calendar));
                ok = false;
            }
            if (!calendar.Occurrence && !calendar.Availability)
            {
                ReportDiagnostic(spc, ManifestDiagnostics.CalendarWithoutRole(calendar));
                ok = false;
            }
            if (!KebabName.IsValid(calendar.CalendarName, maxLength: 128, allowSystemPrefix: false))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.InvalidCalendarName(calendar));
                ok = false;
            }
            else if (duplicateNames.Contains(calendar.CalendarName))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.DuplicateCalendarName(calendar));
                ok = false;
            }
            if (ok)
            {
                whole.Add(calendar);
            }
        }
        return [.. whole.OrderBy(c => c.CalendarName, StringComparer.Ordinal)];
    }

    /// <summary>The manifest names the class in a <c>typeof</c>, which a type parameter or a private type would break.</summary>
    private static bool IsNameable(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (t.IsGenericType || t.DeclaredAccessibility == Accessibility.Private)
            {
                return false;
            }
        }
        return true;
    }
}
