namespace Acta;

/// <summary>
/// Behavior for the startup check that every alerting definition resolves to a configured alert channel
/// in its namespace.
/// </summary>
public enum AlertChannelValidationMode : byte
{
    /// <summary>Skip the routing check entirely.</summary>
    Off = 0,

    /// <summary>Log a structured warning per definition that routes to an unregistered channel; continue.</summary>
    Warn = 1,

    /// <summary>Throw at worker initialization if any alerting definition routes to an unregistered channel.</summary>
    Fail = 2,
}
