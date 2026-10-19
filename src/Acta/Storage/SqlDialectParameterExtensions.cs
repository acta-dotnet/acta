using System.Data.Common;

namespace Acta;

/// <summary>
/// Binds generated column / value metadata straight to a provider parameter:
/// <c>dialect.CreateParameter(ActaSchema.Schedule.Name, command.ScheduleName)</c>. Sugar over
/// <see cref="DbParams.For{T}(DbColumnSpec{T}, object?)"/> plus the provider's single normalized
/// <see cref="ISqlDialect.CreateParameter(DbParameterSpec)"/> seam, so the ordinary binding path
/// never names the intermediate spec. Code that needs the spec itself (building a
/// <see cref="DbParameterSpec"/> list) still calls <c>DbParams.For</c> directly.
/// </summary>
internal static class SqlDialectParameterExtensions
{
    public static DbParameter CreateParameter<T>(this ISqlDialect dialect, DbColumnSpec<T> spec, object? value) =>
        dialect.CreateParameter(DbParams.For(spec, value));

    public static DbParameter CreateParameter<T>(this ISqlDialect dialect, DbValueSpec<T> spec, T value) =>
        dialect.CreateParameter(DbParams.For(spec, value));
}
