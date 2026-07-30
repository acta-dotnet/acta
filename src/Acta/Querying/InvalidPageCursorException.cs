namespace Acta;

/// <summary>
/// Thrown when a list query receives a cursor that cannot be decoded or that was issued by a
/// different operation, ordering, or filter set. Endpoints map it to a 400 response.
/// </summary>
public sealed class InvalidPageCursorException : ArgumentException
{
    public InvalidPageCursorException(string message)
        : base(message) { }
}
