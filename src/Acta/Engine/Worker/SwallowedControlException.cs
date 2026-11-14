namespace Acta;

/// <summary>
/// A handler returned normally after a call on its context ended the attempt by throwing a control-flow exception: a
/// wait, a sleep, or a control verb. The code after the catch that swallowed it ran as if the call had returned, so the
/// attempt fails for good, since a retry would swallow it again. The swallowed exception is the inner one.
/// </summary>
internal sealed class SwallowedControlException(JobControlException control)
    : ActaException(
        $"The handler went on after {Describe(control)} ended the attempt by throwing a JobControlException. Let it through: "
            + "catch (Exception ex) when (ex is not JobControlException).",
        control
    )
{
    private static string Describe(JobControlException control) =>
        control switch
        {
            SignalSuspendSignal { SignalName: var name } when name.StartsWith(RaiseChildLatch.NamePrefix, StringComparison.Ordinal) =>
                $"the wait for child job {name[RaiseChildLatch.NamePrefix.Length..]}",
            SignalSuspendSignal { SignalName: var name } => $"the wait for signal '{name}'",
            SuspendSignal => "a sleep",
            WaitTimeoutSignal { WaitName: var name } => $"the expired wait '{name}'",
            RescheduleJobException => "RescheduleAsync",
            HandlerFailException => "FailAsync",
            HandlerCancelException => "CancelAsync",
            HandlerPauseException => "PauseAsync",
            _ => control.GetType().Name,
        };
}
