namespace Acta.Bench;

/// <summary>The database server cannot run as the protocol or the run asks; the run stops as a harness error.</summary>
internal sealed class ServerSetupException(string message) : InvalidOperationException(message);
