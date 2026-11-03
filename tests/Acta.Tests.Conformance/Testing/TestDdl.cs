namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Runs a test's schema work on SQL Server one batch of DDL at a time in the process: a migration, a drop, a probe table,
/// or a catalog read. Two of them at once deadlock on SQL Server's own catalog tables, and the deadlock census fails the
/// run on any, so the suite serializes them instead of retrying. A gated call never takes the gate again inside itself.
/// </summary>
public static class TestDdl
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        await s_gate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            s_gate.Release();
        }
    }

    public static Task RunAsync(Func<Task> work) =>
        RunAsync(async () =>
        {
            await work();
            return true;
        });
}
