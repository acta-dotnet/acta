namespace Acta.Runtime;

/// <summary>The name search the two namespace lists share.</summary>
internal static class NamespaceNames
{
    /// <summary>
    /// The validated search fragment and the <c>LIKE</c> pattern the SQL matches it with. Validation admits
    /// only name characters, so the pattern holds no wildcard but its own.
    /// </summary>
    public static (string? Fragment, string? Pattern) Search(string? nameContains)
    {
        var fragment = QueryValidation.ValidateNamespaceFragment(nameContains, nameof(ListNamespaceNamesQuery.NameContains));
        return (fragment, fragment is null ? null : "%" + fragment + "%");
    }
}
