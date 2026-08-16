using Microsoft.AspNetCore.Builder;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Endpoint metadata naming the JSON body an endpoint reads through
/// <see cref="ControlEndpointValidation"/>. Reading the body by hand rather than as a bound parameter
/// keeps the handlers in charge of their own 415 / 400 answers, but leaves the generated OpenAPI
/// document blind to the payload, so each write endpoint declares its body with this record and the
/// (test-only) document generation renders it - the same arrangement <see cref="QueryParameterDoc"/>
/// already uses for the query surface, and the product assembly stays free of any OpenAPI dependency.
/// </summary>
/// <remarks>
/// Deliberately not the framework's <c>Accepts&lt;T&gt;</c>: the metadata that writes emits
/// (<c>IAcceptsMetadata</c>) is read by the routing matcher, which then rejects a request whose
/// Content-Type the declaration does not list - including a request with no body at all - before the
/// handler runs. These endpoints answer those cases themselves, with a problem document that says
/// which content type is wanted, and an optional body has to reach a handler that treats absence as
/// "act with defaults".
/// </remarks>
internal sealed record RequestBodyDoc(Type BodyType, bool Optional);

internal static class RequestBodyDocExtensions
{
    /// <summary>
    /// Declares the <c>application/json</c> body <typeparamref name="TBody"/> this endpoint reads.
    /// <paramref name="optional"/> marks a body the endpoint acts without.
    /// </summary>
    public static RouteHandlerBuilder AcceptsJson<TBody>(this RouteHandlerBuilder builder, bool optional = false)
        where TBody : notnull => builder.WithMetadata(new RequestBodyDoc(typeof(TBody), optional));
}
