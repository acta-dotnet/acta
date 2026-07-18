using Microsoft.Extensions.Options;

namespace Acta.Sqlite.Configuration;

/// <summary>Enforces SQLite's single writable database schema at host startup.</summary>
internal sealed class SqliteProviderOptionsValidator : IValidateOptions<SqliteProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, SqliteProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Equals(options.Schema, "main", StringComparison.Ordinal)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("SqliteProviderOptions.Schema must be 'main'; attached SQLite databases are not supported.");
    }
}
