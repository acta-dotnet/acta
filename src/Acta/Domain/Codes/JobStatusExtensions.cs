namespace Acta;

public static partial class JobStatusExtensions
{
    extension(JobStatusCode value)
    {
        public bool IsFinished => value is JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled;
    }
}
