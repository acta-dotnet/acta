<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import type { JobControlResponse } from '../api.ts';
  import { validateSignalName, validateSignalPayload } from './signalDrawerState.ts';

  let {
    jobRef,
    embedded = false,
    onSent = () => {}
  }: { jobRef: string; embedded?: boolean; onSent?: () => void } = $props();

  let open = $state(false);
  let name = $state('');
  let payloadText = $state('');
  let message = $state('');
  let messageKind = $state('');

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  let nameError = $derived(name ? validateSignalName(name) : null);
  let payloadResult = $derived(validateSignalPayload(payloadText));
  let canSend = $derived(name.trim() !== '' && !nameError && payloadResult.ok);

  // Same shape as every other job verb (jobs/{jobRef}/{verb} -> JobControlResponse), but the body
  // is the caller's own JSON payload verbatim - not a reason-merged object - so this uses rawBody
  // rather than body: an empty payloadText resolves to `undefined`, which controlRequest sends as a
  // request with zero body bytes (presence-only signal); any parsed JSON goes through unmodified.
  const mutation = useControlMutation<{ jobRef: string; name: string; payload: unknown }, JobControlResponse>({
    path: (vars) => `jobs/${vars.jobRef}/signals/${vars.name}`,
    rawBody: (vars) => vars.payload,
    notFound: (vars) => ({ jobRef: vars.jobRef, action: 'notFound', status: null, message: 'Job not found.' }),
    invalidateKeys: () => [['jobs']] as const
  });
  let busy = $derived(mutation.isPending);

  export function openForm(suggestedName = '') {
    if (!canControlNow) return;
    name = suggestedName;
    payloadText = '';
    message = '';
    open = true;
  }

  function cancel() {
    open = false;
    message = '';
  }

  async function send() {
    if (!canSend) {
      return;
    }
    const vars = { jobRef, name: name.trim(), payload: payloadResult.value };
    open = false;
    message = '';
    try {
      const result = await mutation.mutateAsync(vars);
      message = result.message;
      messageKind = result.action === 'applied' ? 'ok' : 'warn';
      onSent();
    } catch (e) {
      message = (e as Error).message;
      messageKind = 'bad';
    }
  }
</script>

<section class:panel={!embedded} class:detail-panel={embedded}>
  <h2>Signals</h2>
  {#if !open}
    <div class="control-row">
      {#if canControlNow}
        <button disabled={busy} onclick={() => openForm()}>Raise signal</button>
      {:else}
        <span class="dim">Signal controls are disabled on this host.</span>
      {/if}
    </div>
  {:else}
    <label class="detail-field">
      <span>Signal name</span>
      <input bind:value={name} placeholder="e.g. payment-received" aria-invalid={!!nameError} />
    </label>
    {#if nameError}<div class="field-error">{nameError}</div>{/if}

    <label class="detail-field">
      <span>JSON payload <span class="field-hint">optional — leave empty for a presence-only signal</span></span>
      <textarea class="mono" rows="5" bind:value={payloadText} placeholder={'{ "key": "value" }'}></textarea>
    </label>
    {#if !payloadResult.ok}<div class="field-error">{payloadResult.error}</div>{/if}

    <div class="detail-form-actions">
      <button class="primary" disabled={busy || !canSend} onclick={send}>Send signal</button>
      <button onclick={cancel}>Cancel</button>
    </div>
  {/if}

  {#if message}
    <div class="control-message {messageKind}" role="status">{message}</div>
  {/if}
</section>

<style>
  .field-error {
    color: var(--bad);
    font-size: var(--text-sm);
    margin: 6px 0 0;
  }
  /* Normal-weight, muted sub-label inline in the field's bold label row. */
  .field-hint { font-weight: 400; color: var(--muted); }
  /* .detail-field's `font: inherit` on inputs would otherwise drop the mono family for the JSON body. */
  .detail-field textarea.mono { font-family: var(--mono); }
</style>
