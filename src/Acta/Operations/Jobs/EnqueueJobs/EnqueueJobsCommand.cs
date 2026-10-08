using System.Data.Common;

namespace Acta;

/// <summary>The jobs to enqueue in one statement, through the caller's <c>Transaction</c> when it is set.</summary>
internal sealed record EnqueueJobsCommand(IReadOnlyList<EnqueueJobRequest> Requests, DbTransaction? Transaction = null);
