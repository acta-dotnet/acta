namespace Acta;

/// <summary>
/// A schedule's calendar binding resolves to nothing this host can evaluate: a code calendar the process does not
/// declare, or a catalog version the catalog no longer holds. The admitter parks such a schedule as an evaluation
/// error; a preview throws this, which the HTTP layer answers as a 409 problem.
/// </summary>
public sealed class CalendarUnavailableException(string message) : InvalidOperationException(message);
