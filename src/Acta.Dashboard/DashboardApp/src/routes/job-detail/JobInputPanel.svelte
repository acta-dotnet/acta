<script lang="ts">
  import { payloadRoutes, type JobControlResponse } from '../../api.ts';
  import { useControlMutation } from '../../lib/useControlMutation.ts';
  import type { RawJson } from '../../lib/rawJson.ts';
  import { updateBody, updateOutcomeMessage, inputUpdatable } from './inputUpdate.ts';
  import StoredPayload from './StoredPayload.svelte';

  // The stored input, stated by the job's read and read from its own route by StoredPayload once `active`, renders for
  // everyone. It is read again whenever the job's version moves, which every input update does. It is editable only
  // when the operator can control, the job is not in flight, and its body was read, since the editor edits that text.
  // The update PUT is a compare-and-set on the version the edited body was read at; on success it asks JobDetail to
  // refetch the detail query (onUpdated).
  let { jobRef, formatId, byteLength, version, active = true, status, canControl = false, onUpdated = () => {} }: {
    jobRef: string;
    formatId: number;
    byteLength: number;
    // The job's version, which the input is read at and an update names as expectedVersion.
    version: number;
    active?: boolean;
    status: string;
    canControl?: boolean;
    onUpdated?: () => void;
  } = $props();

  // PayloadView itself offers the editor only for a well-formed json input that is current.
  let updatable = $derived(canControl && inputUpdatable(status));
  let tooLarge = $state(false);
  let editing = $state(false);
  let reason = $state('');
  let message = $state('');
  let messageKind = $state('');

  // Leaving the editor drops the typed reason: it would otherwise sit invisible and ride the next update.
  $effect(() => {
    if (!editing) reason = '';
  });

  const mutation = useControlMutation<
    { jobRef: string; body: { input: RawJson }; expectedVersion: number; reason?: string },
    JobControlResponse
  >({
    path: (vars) => `jobs/${vars.jobRef}/input`,
    method: 'PUT',
    body: (vars) => ({ ...vars.body, expectedVersion: vars.expectedVersion }),
    notFound: () => ({ action: 'not-found', status: null }),
    versionConflict: () => ({ action: 'version-conflict', status: null }),
    invalidateKeys: () => [['jobs']] as const
  });

  // Only json inputs are editable, so the draft is JSON text and travels verbatim. Anything but an applied update
  // keeps the editor open with the draft in it.
  async function update(text: string, readAt: string): Promise<boolean> {
    message = '';
    const built = updateBody(text);
    if ('error' in built) {
      message = built.error;
      messageKind = 'bad';
      return false;
    }
    try {
      const result = await mutation.mutateAsync({ jobRef, body: built.body, expectedVersion: Number(readAt), reason });
      const outcome = updateOutcomeMessage(result.action, result.status);
      message = outcome.text;
      messageKind = outcome.kind;
      if (result.action !== 'applied') return false;
      reason = '';
      onUpdated();
      return true;
    } catch (error) {
      message = error instanceof Error ? error.message : String(error);
      messageKind = 'bad';
      return false;
    }
  }
</script>

<section class="detail-panel" aria-label="Job input">
  <h2>Input</h2>
  {#if editing}
    <label class="detail-field">
      <span>Reason</span>
      <input bind:value={reason} maxlength="512" placeholder="Why are you updating the input? (optional)" disabled={mutation.isPending} />
    </label>
  {/if}
  <StoredPayload
    path={payloadRoutes.input(jobRef)}
    {formatId}
    {byteLength}
    fileName={jobRef + '-input'}
    label="input"
    version={String(version)}
    {active}
    editable={updatable}
    bind:editing
    bind:over={tooLarge}
    onSave={update} />
  {#if updatable && tooLarge}<p class="dim">This input is too large to edit here.</p>{/if}
  {#if message}<div class="control-message {messageKind}" role="status">{message}</div>{/if}
</section>
