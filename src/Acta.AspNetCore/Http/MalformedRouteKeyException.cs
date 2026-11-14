namespace Acta.AspNetCore;

/// <summary>A route segment that is not a valid ref, name, or key: caller input, so a 400 whose detail is the message.</summary>
internal sealed class MalformedRouteKeyException(string message) : ActaException(message)
{
    public MalformedRouteKeyException(string segment, string entity)
        : this($"{segment} is not a valid {entity} ref.") { }
}
