namespace Acta;

/// <summary>
/// A schedule's calendar name resolves to nothing this host can evaluate: the process declares no calendar of that
/// name in that role, and neither the namespace's catalog nor the shared one has a current version of it. The admitter
/// parks such a schedule as an evaluation error; a preview throws this, which the HTTP layer answers as a 409 problem.
/// </summary>
public sealed class CalendarUnavailableException(string message) : InvalidOperationException(message);
