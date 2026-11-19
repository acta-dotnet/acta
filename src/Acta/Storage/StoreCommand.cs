using System.Text.Json;

namespace Acta;

/// <summary>
/// Addresses one operation's SQL by the stem of its file, which selects an installed routine or an inline
/// body in the provider's catalog. The stem is unique per dialect and names the routine too, so one handler
/// maps to each provider's own shape. <paramref name="ReturnsPayload"/> says a row of its result carries a stored payload,
/// which a provider may read in the way that suits a large value.
/// </summary>
internal readonly record struct StoreCommand(string Operation, bool ReturnsPayload = false)
{
    /// <summary>The snake_case routine name a routine provider invokes for this command.</summary>
    public string RoutineName => JsonNamingPolicy.SnakeCaseLower.ConvertName(Operation);
}
