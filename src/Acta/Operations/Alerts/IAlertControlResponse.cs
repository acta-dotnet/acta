namespace Acta;

/// <summary>
/// What an alert control verb did: Applied, or NotFound when no alert carries the ref. Never Rejected, because
/// nothing beyond the alert existing guards either verb.
/// </summary>
public interface IAlertControlResponse
{
    /// <summary>The targeted alert's public ref.</summary>
    AlertRef AlertRef { get; }

    /// <summary>The outcome.</summary>
    ControlAction Action { get; }

    /// <summary>When the alert was acknowledged; null until it is.</summary>
    DateTime? AcknowledgedAtUtc { get; }

    /// <summary>When the alert was resolved; null until it is.</summary>
    DateTime? ResolvedAtUtc { get; }
}
