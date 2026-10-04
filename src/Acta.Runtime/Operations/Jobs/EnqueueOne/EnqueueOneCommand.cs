using System.Data.Common;

namespace Acta.Runtime;

/// <summary>One job to enqueue, through the caller's <c>Transaction</c> when it is set.</summary>
internal sealed record EnqueueOneCommand(JobEnqueueRequest Request, DbTransaction? Transaction = null);
