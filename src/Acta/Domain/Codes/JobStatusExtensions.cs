namespace Acta;

public static partial class JobStatusExtensions
{
    extension(JobStatusCode value)
    {
        public bool IsTerminal => value is JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled;
    }
}
