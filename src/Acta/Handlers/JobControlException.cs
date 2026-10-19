namespace Acta;

/// <summary>
/// Base for the non-error control-flow signals a handler raises to steer its own scheduling: the host catches the
/// base type and translates the concrete signal into a scheduler action (re-arm, suspend) without charging the
/// failure budget, and <paramref name="message"/> flows into the re-arm reason. Do not catch it in user code:
/// swallowing it turns a deliberate reschedule, sleep, or termination into a silent fall-through. The family is
/// closed to the framework; its public members are <see cref="RescheduleJobException"/> and the
/// <see cref="HandlerControlException"/> family.
/// </summary>
public abstract class JobControlException(string? message) : Exception(message) { }
