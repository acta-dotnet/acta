namespace Acta;

/// <summary>
/// The always-real database server clock. <c>UseActa</c> registers it unconditionally so the
/// worker-init clock-skew guard measures the host offset against the true DB clock even when a test
/// has replaced <see cref="IActaClock"/> with a deterministic fake for schedule determinism; only the
/// chaos hooks wrap it, to inject faults. <c>UseActa</c> also defaults <see cref="IActaClock"/> to it.
/// </summary>
internal interface IClockOperations : IActaClock;
