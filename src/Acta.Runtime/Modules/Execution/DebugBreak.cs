namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// One-shot ambient flag for the CLI <c>jobs debug --break</c> path. Set true by
/// <c>CliCommandRunner.DebugAsync</c> around the single in-process run and read at the handler seam
/// in <c>JobExecution</c>, where it raises the debugger just before the user's handler is invoked.
/// AsyncLocal so it flows down the run's async chain without threading a parameter through the
/// runtime signatures; it is only ever set on the CLI debug path, so a normal worker never breaks.
/// </summary>
internal static class DebugBreak
{
    private static readonly AsyncLocal<bool> _requested = new();

    public static bool Requested
    {
        get => _requested.Value;
        set => _requested.Value = value;
    }
}
