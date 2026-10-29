namespace Acta;

/// <summary>
/// Addresses one named schedule for a control verb by its path: the namespace, the scheduled job's name, and the
/// schedule's name, the way the HTTP routes and the dashboard address it. The path resolves through the definition to
/// its permanent slot job, which carries no key of its own; a schedule has no ref of its own.
/// </summary>
public sealed record ScheduleLookup(string JobNamespace, string JobName, string ScheduleName);
