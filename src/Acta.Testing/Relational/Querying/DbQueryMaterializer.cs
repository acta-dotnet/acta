using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Acta.Relational.Commands;
using Acta.Relational.Schema;

namespace Acta.Testing.Relational.Querying;

/// <summary>
/// Materializes one row into an entity (via generator-emitted <c>EntityBinder</c>) or a
/// projection (via reflection over its primary constructor).
/// </summary>
internal static class Materializer
{
    public static T MaterializeEntity<T>(DbDataReader reader)
        where T : class, IEntity => EntityBinder.Bind<T>(reader);

    public static TProjection MaterializeProjection<TProjection>(DbDataReader reader, IReadOnlyList<DbColumnSpec> columns)
        where TProjection : class
    {
        var t = typeof(TProjection);
        var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(c => c.GetParameters().Length > 0)
            .Where(c => !(c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == t))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor is null)
        {
            var instance =
                Activator.CreateInstance(t) ?? throw new InvalidOperationException($"Projection '{t.Name}' has no usable constructor.");
            for (var i = 0; i < columns.Count; i++)
            {
                var prop = t.GetProperty(columns[i].ClrPropertyName);
                prop?.SetValue(instance, ReadAndConvert(reader, i, prop.PropertyType));
            }
            return (TProjection)instance;
        }

        var args = new object?[ctor.GetParameters().Length];
        for (var i = 0; i < args.Length; i++)
        {
            args[i] = ReadAndConvert(reader, i, ctor.GetParameters()[i].ParameterType);
        }
        return (TProjection)ctor.Invoke(args);
    }

    private static object? ReadAndConvert(DbDataReader reader, int ord, Type targetType)
    {
        if (reader.IsDBNull(ord))
        {
            return null;
        }
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var raw = reader.GetValue(ord);

        if (t.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(t);
            var num = Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
            return Enum.ToObject(t, num);
        }
        if (t == typeof(byte[]))
        {
            return (byte[])raw;
        }
        if (t == typeof(DateTime))
        {
            // SQLite returns epoch milliseconds (long); Postgres/SQL Server return a DateTime.
            return DbCellCoercion.ToUtc(raw);
        }
        if (t == typeof(Guid) || t == typeof(string))
        {
            return raw;
        }
        return Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
    }
}
