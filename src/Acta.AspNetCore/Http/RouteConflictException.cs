namespace Acta.AspNetCore;

/// <summary>The request is valid but cannot be answered here or now: the 409 under <see cref="Title"/>, with this message as the detail.</summary>
internal sealed class RouteConflictException(string title, string detail) : Exception(detail)
{
    public string Title { get; } = title;
}
