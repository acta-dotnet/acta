namespace Acta.AspNetCore;

/// <summary>
/// A route segment that is not a valid ref, name, or key: caller input, so a 400 whose detail is the message. It sets
/// no parameter name, which would append "(Parameter '...')" to that detail.
/// </summary>
internal sealed class MalformedRouteKeyException(string message) : ArgumentException(message)
{
    public MalformedRouteKeyException(string segment, string entity)
        : this($"{segment} is not a valid {entity} ref.") { }
}
