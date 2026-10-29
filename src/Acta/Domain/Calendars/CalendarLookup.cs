namespace Acta;

/// <summary>
/// Addresses one catalog calendar family by its namespace and name, the way the calendar verbs take it. Both are names,
/// trimmed and lowercased; the namespace <c>sys</c> holds the shared families, and a calendar name may not be <c>sys</c>
/// or start with <c>sys.</c>. A calendar name runs to 128 characters, a namespace to 64.
/// </summary>
public sealed record CalendarLookup(string JobNamespace, string CalendarName);
