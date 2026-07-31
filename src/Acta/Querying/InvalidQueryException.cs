namespace Acta;

/// <summary>
/// Thrown when a read/list query carries a filter, page-size, or tag value that fails validation.
/// Endpoints map it to a 400 response; any other exception is a server fault and stays on the
/// sanitized 500 path. Derives from <see cref="ArgumentException"/> so direct API callers keep
/// their existing catch semantics.
/// </summary>
public sealed class InvalidQueryException(string message) : ArgumentException(message) { }
