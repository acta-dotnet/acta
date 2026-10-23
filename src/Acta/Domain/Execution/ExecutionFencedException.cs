using System.Data.Common;

namespace Acta;

/// <summary>
/// A handler-originated write found its job's <c>jobs_state</c> row no longer Executing under the attempt's
/// <see cref="ExecutionFence"/>, so it changed nothing. The executor ends the attempt as it ends a lost lease: no
/// completion, no retry charged, and the row stays with whoever holds it now.
/// </summary>
internal sealed class ExecutionFencedException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>The token a fenced routine raises, matched anywhere in the message because provider wrappers prepend their own.</summary>
    public const string Token = "ACTA:EXECUTION_FENCED:";

    /// <summary>Awaits one fenced statement and raises its refusal as this exception.</summary>
    public static async Task<T> TranslateAsync<T>(Task<T> statement)
    {
        try
        {
            return await statement;
        }
        catch (DbException ex) when (From(ex) is { } fenced)
        {
            throw fenced;
        }
    }

    /// <summary>The refusal a provider error carries, or null when it is another error.</summary>
    public static ExecutionFencedException? From(DbException ex)
    {
        var at = ex.Message.IndexOf(Token, StringComparison.Ordinal);
        return at < 0 ? null : new ExecutionFencedException(ex.Message[(at + Token.Length)..], ex);
    }
}
