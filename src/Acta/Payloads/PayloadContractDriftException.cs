namespace Acta;

/// <summary>
/// Thrown at worker startup under <see cref="PayloadContractDriftMode.Fail"/> when an eligible
/// registration would change one or more definitions' contract columns.
/// </summary>
public sealed class PayloadContractDriftException(string message) : Exception(message) { }
