namespace Acta.Testing;

/// <summary>Looks up a column spec by CLR property name.</summary>
internal static class EntitySpecLookups
{
    public static DbColumnSpec? FindByClrProperty(this DbEntitySpec spec, string clrPropertyName)
    {
        foreach (var col in spec.Columns)
        {
            if (col.ClrPropertyName == clrPropertyName)
            {
                return col;
            }
        }
        return null;
    }
}
