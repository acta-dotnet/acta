using System.Data.Common;

namespace Acta;

/// <summary>One job to enqueue, through the caller's <c>Transaction</c> when it is set.</summary>
internal sealed record EnqueueJobCommand(EnqueueJobRequest Request, DbTransaction? Transaction = null);
