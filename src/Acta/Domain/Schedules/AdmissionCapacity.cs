namespace Acta;

/// <summary>
/// Whether a due occurrence can be admitted right now, as the admitter read it: a permanent slot is Idle, or a
/// per-occurrence schedule is under its outstanding limit (<see cref="Available"/>); the slot is busy or the limit is
/// reached (<see cref="Full"/>, settlement wakes the schedule); or the slot is held, cancelled, or failed terminally
/// (<see cref="Unavailable"/>, a job resume or restart wakes it).
/// </summary>
internal enum AdmissionCapacity : byte
{
    Available,
    Full,
    Unavailable,
}
