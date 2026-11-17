namespace Acta;

public static class WorkerStatusExtensions
{
    extension(WorkerStatusCode value)
    {
        public bool IsFinished => value is WorkerStatusCode.Stopped or WorkerStatusCode.Dead;
    }
}
