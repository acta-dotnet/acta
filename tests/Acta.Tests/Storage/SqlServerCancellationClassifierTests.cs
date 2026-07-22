using System.Reflection;
using Acta.SqlServer.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// <see cref="SqlServerDialect.IsCancellation"/> classifies the token-cancel attention signal
/// (SqlException Number 0 at severity Class 11, verified live) without swallowing a genuine
/// connection-fatal fault, which SqlClient also surfaces under the Number-0 catch-all but at
/// severity Class 20 or higher.
/// </summary>
public class SqlServerCancellationClassifierTests
{
    private static readonly SqlServerDialect Dialect = new();

    [Fact]
    public void Number_0_class_11_is_cancellation()
    {
        // The shape a token-cancelled command produces: "A severe error occurred on the current
        // command" at the attention-signal severity.
        Assert.True(Dialect.IsCancellation(MakeSqlException(number: 0, errorClass: 11)));
    }

    [Fact]
    public void Number_3980_is_cancellation()
    {
        // "The batch is aborted" by a client abort signal: an unambiguous cancellation code.
        Assert.True(Dialect.IsCancellation(MakeSqlException(number: 3980, errorClass: 16)));
    }

    [Fact]
    public void Number_0_at_fatal_class_is_not_cancellation()
    {
        // A connection-terminating fault (KILL, transport reset) also carries Number 0, but at a
        // fatal severity; classifying it as a clean cancel would swallow the error unlogged.
        Assert.False(Dialect.IsCancellation(MakeSqlException(number: 0, errorClass: 20)));
    }

    [Fact]
    public void Non_sql_exception_is_not_cancellation() => Assert.False(Dialect.IsCancellation(new InvalidOperationException()));

    // SqlException has no public constructor; fabricate one at a chosen Number/Class through the
    // stable internal ctors so the classifier can be unit-tested off a live server.
    private static SqlException MakeSqlException(int number, byte errorClass)
    {
        var errorCtor = typeof(SqlError).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)],
            modifiers: null
        )!;
        var error = (SqlError)errorCtor.Invoke([number, (byte)0, errorClass, "server", "message", "procedure", 0, null]);
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(collection, [error]);
        var create = typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(SqlErrorCollection), typeof(string)],
            modifiers: null
        )!;
        return (SqlException)create.Invoke(null, [collection, "16.0"])!;
    }
}
