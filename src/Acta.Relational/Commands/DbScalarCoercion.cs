using System.Globalization;

namespace Acta.Relational.Commands;

internal static class DbScalarCoercion
{
    private const string Supported = "byte, short, int, long, bool, string, DateTime, Guid, and nullable value-type variants";

    public static Func<object?, T> Resolve<T>(string operation, string? emptyMessage = null)
    {
        var targetType = typeof(T);
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying is not null)
        {
            if (!IsSupportedRequiredType(underlying))
            {
                throw Unsupported(targetType);
            }

            return value => value is null or DBNull ? default! : (T)Coerce(NotNull(value, operation, emptyMessage), underlying);
        }

        if (!IsSupportedRequiredType(targetType))
        {
            throw Unsupported(targetType);
        }

        return value => (T)Coerce(NotNull(value, operation, emptyMessage), targetType);
    }

    private static bool IsSupportedRequiredType(Type targetType) =>
        targetType == typeof(byte)
        || targetType == typeof(short)
        || targetType == typeof(int)
        || targetType == typeof(long)
        || targetType == typeof(bool)
        || targetType == typeof(string)
        || targetType == typeof(DateTime)
        || targetType == typeof(Guid);

    private static object Coerce(object raw, Type targetType)
    {
        if (targetType == typeof(byte))
        {
            return Convert.ToByte(raw, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(short))
        {
            return Convert.ToInt16(raw, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(int))
        {
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(long))
        {
            return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(bool))
        {
            return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(string))
        {
            return Convert.ToString(raw, CultureInfo.InvariantCulture)!;
        }
        if (targetType == typeof(DateTime))
        {
            return DbCellCoercion.ToUtc(raw);
        }
        if (targetType == typeof(Guid))
        {
            return raw switch
            {
                Guid guid => guid,
                string text => Guid.Parse(text),
                byte[] bytes => new Guid(bytes),
                _ => throw new InvalidOperationException($"Cannot coerce scalar value of type '{raw.GetType().FullName}' to Guid."),
            };
        }

        throw Unsupported(targetType);
    }

    private static object NotNull(object? raw, string operation, string? emptyMessage) =>
        raw is null or DBNull ? throw new InvalidOperationException(emptyMessage ?? $"{operation} returned no value.") : raw;

    private static InvalidOperationException Unsupported(Type targetType) =>
        new(
            $"Acta scalar result type '{targetType.FullName ?? targetType.Name}' is not supported. "
                + $"Supported scalar result types are {Supported}."
        );
}
