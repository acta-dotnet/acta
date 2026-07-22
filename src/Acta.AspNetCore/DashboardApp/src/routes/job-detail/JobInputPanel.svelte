<script lang="ts">
  import { type JobControlResponse, type JobPayloadView } from '../../api.ts';
  import { useControlMutation } from '../../lib/useControlMutation.ts';
  import PayloadView from '../../components/PayloadView.svelte';
  import { amendBody, amendOutcomeMessage, inputAmendable } from './inputAmend.ts';

  // Presentational: the stored input comes from the aggregate detail read (JobDetail owns the fetch and
  // polling), so this panel renders for everyone. The input is editable only when the operator can
  // control and the job is not in flight; the amend POST lives here and, on success, asks JobDetail to
  // refetch the detail query (onAmended) rather than owning a private input query. PayloadView just
  // supplies the validated draft text.
  let { input, jobRef, status, canControl = false, onAmended = () => {} }: {
    input: JobPayloadView;
    jobRef: string;
    status: string;
    canControl?: boolean;
    onAmended?: () => void;
  } = $props();

  // A none-format or over-cap (truncated) input has no wire body to amend into, so the editor never
  // opens for one.
  let editable = $derived(canControl && inputAmendable(status) && input.format !== 'none' && !input.truncated);
  let editing = $state(false);
  let reason = $state('');
  let message = $state('');
  let messageKind = $state('');

  // Leaving the editor drops the typed reason: it would otherwise sit invisible and ride the next amend.
  $effect(() => {
    if (!editing) reason = '';
  });

  const mutation = useControlMutation<{ jobRef: string; body: Record<string, unknown>; reason?: string }, JobControlResponse>({
    path: (vars) => `jobs/${vars.jobRef}/input`,
    body: (vars) => vars.body,
    notFound: (vars) => ({ jobRef: vars.jobRef, action: 'notFound', status: null, message: 'Job not found.' }),
    invalidateKeys: () => [['jobs']] as const
  });

  // Dispatch on the job's stored format so the amend speaks the right wire field (a text job amends
  // text, everything else json). Binary and none formats are not editable, so only json/text reach here.
  async function amend(text: string): Promise<void> {
    message = '';
    const built = amendBody(text, input.format);
    if ('error' in built) {
      message = built.error;
      messageKind = 'bad';
      return;
    }
    try {
      const result = await mutation.mutateAsync({ jobRef, body: built.body, reason });
      const outcome = amendOutcomeMessage(result.action, result.message);
      message = outcome.text;
      messageKind = outcome.kind;
      if (result.action === 'applied') {
        reason = '';
        onAmended();
      }
    } catch (error) {
      message = error instanceof Error ? error.message : String(error);
      messageKind = 'bad';
    }
  }
</script>

<section class="detail-panel" aria-label="Job input">
  <h2>Input</h2>
  {#if editing}
    <label class="detail-field">
      <span>Reason</span>
      <input bind:value={reason} maxlength="512" placeholder="Why are you amending the input? (optional)" disabled={mutation.isPending} />
    </label>
  {/if}
  <PayloadView payload={input} {editable} bind:editing onSave={amend} />
  {#if message}<div class="control-message {messageKind}" role="status">{message}</div>{/if}
</section>
