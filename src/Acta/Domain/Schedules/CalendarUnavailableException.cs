namespace Acta;

/// <summary>
/// A schedule's calendar name resolves to nothing: neither the namespace's calendar catalog nor the shared one has a
/// published version of that name in that role. The admitter parks such a schedule as an evaluation error; a preview
/// throws this, which the HTTP layer answers as a 409 problem.
/// </summary>
public sealed class CalendarUnavailableException(string message) : InvalidOperationException(message);
