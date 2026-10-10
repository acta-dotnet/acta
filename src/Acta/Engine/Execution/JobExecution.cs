using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The admit-invoke body of a started attempt: <see cref="ExecutionAdmission"/>, which completes a refused attempt,
/// then <see cref="HandlerInvocation"/>, which completes an admitted one. <see cref="JobExecutor"/> is the frame around
/// it, and <see cref="JobFinalizer"/> writes the <see cref="ExecutionCompletion"/> it returns.
/// </summary>
internal sealed class JobExecution(
    IJobPayloadSerializerRegistry serializers,
    IOptions<JobsOptions> options,
    JobBehaviorPipeline pipeline,
    ILogger log
)
{
    private readonly ExecutionAdmission _admission = new(log, options.Value.ConcurrencyKeyBounceDelaySeconds);
    private readonly HandlerInvocation _invocation = new(serializers, pipeline, options.Value.MaxInlinePayloadBytes, log);

    /// <summary>The attempt's completion, or null when worker shutdown interrupted the handler.</summary>
    public async Task<ExecutionCompletion?> RunAsync(
        IServiceProvider attemptServices,
        JobDescriptor descriptor,
        ClaimedJob job,
        RuntimeJobContext jobContext,
        RecurringFirePlan? fire,
        CancellationToken ct
    )
    {
        try
        {
            // A fire that only ends timed pauses with nothing due takes no slot, no turn, and no handler call;
            // it completes as a re-arm that applies the advances (ScheduleWalker.PlanFire).
            return fire is { NothingDue: true }
                ? new ExecutionCompletion(CompletionKind.Rescheduled)
                {
                    ReasonMessage = "A timed schedule pause ended with no occurrence due; the handler did not run.",
                }
                : await _admission.AdmitAsync(descriptor, job, jobContext, ct)
                    ?? await _invocation.InvokeAsync(attemptServices, descriptor, job, jobContext, ct);
        }
        finally
        {
            // Mutual exclusion covers the handler, not the completion write: the slot admission took goes back on
            // every exit, a refusal, the handler's end, or worker shutdown. Release is best-effort, non-throwing, and
            // a no-op when no slot was taken; a failed release self-heals via the lock's TTL.
            await jobContext.ReleaseConcurrencySlotAsync(CancellationToken.None);
        }
    }
}
