namespace Acta;

/// <summary>
/// Thrown by a deploy under <see cref="PayloadContractDriftMode.Fail"/> when it would change one or
/// more definitions' contract columns.
/// </summary>
public sealed class PayloadContractDriftException(string message) : Exception(message) { }
