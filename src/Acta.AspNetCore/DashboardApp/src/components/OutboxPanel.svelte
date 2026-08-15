<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import { displayFormatter } from '../format.ts';
  import ConfirmAction from './ConfirmAction.svelte';
  import DataTable from './DataTable.svelte';

  // One relay source line from outbox/sources: the sys.outbox slot's parsed tick accounting.
  // backlog/quarantineTotal are null before the slot's first successful tick, never zero-faked.
  interface OutboxSourceView {
    jobNamespace: string;
    slotJobRef: string;
    lastTickSummary: string | null;
    backlog: number | null;
    quarantineTotal: number | null;
    isLocal: boolean;
  }

  interface OutboxControlResponse {
    action: string;
    pendingSinceUtc: string | null;
    message: string;
  }

  let {
    sources = [],
    onChanged = () => {}
  }: {
    sources?: OutboxSourceView[];
    onChanged?: () => void;
  } = $props();

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  let message = $state('');
  let messageKind = $state('');
  let confirming = $state<{ verb: 'requeue' | 'discard'; jobNamespace: string; quarantined: number } | null>(null);

  // Requeue/discard park a durable command on the source's relay slot; 202 accepted means "the next
  // relay pass applies it", so success invalidates the overview snapshot rather than expecting the
  // counters to have moved already.
  const mutation = useControlMutation<{ verb: 'requeue' | 'discard'; jobNamespace: string; reason?: string }, OutboxControlResponse>({
    path: (vars) => `outbox/${vars.verb}`,
    rawBody: (vars) => ({ jobNamespace: vars.jobNamespace, reasonMessage: vars.reason?.trim() || null }),
    notFound: () => ({ action: 'notFound', pendingSinceUtc: null, message: 'No outbox relay slot exists for that namespace.' }),
    invalidateKeys: () => [['overview']] as const
  });
  let busy = $derived(mutation.isPending);

  async function run(verb: 'requeue' | 'discard', jobNamespace: string, reason: string) {
    confirming = null;
    message = '';
    try {
      const result = await mutation.mutateAsync({ verb, jobNamespace, reason });
      // Accepted is the verb's success: parked, applied by the next relay pass. Rejected (a pending
      // command already parked) is a warning carrying its park instant, never a hard error.
      messageKind = result.action === 'accepted' ? 'ok' : 'warn';
      message =
        result.pendingSinceUtc && result.action === 'rejected'
          ? result.message + ' Pending since ' + displayFormatter.timestamp(result.pendingSinceUtc) + '.'
          : result.message;
      if (result.action === 'accepted') onChanged();
    } catch (e) {
      message = (e as Error).message;
      messageKind = 'bad';
    }
  }
</script>

<div class="panel">
  <h2>Outbox relays</h2>
  <DataTable ledger>
    <caption class="sr-only">Outbox relay sources</caption>
    <thead>
      <tr>
        <th>Namespace</th>
        <th>Backlog</th>
        <th>Quarantined</th>
        <th>Last tick</th>
        {#if canControlNow}<th class="actions-col">Actions</th>{/if}
      </tr>
    </thead>
    <tbody>
      {#each sources as source}
        <tr class:trouble={(source.quarantineTotal ?? 0) > 0}>
          <td>{source.jobNamespace}</td>
          <td>{source.backlog === null ? '-' : displayFormatter.number(source.backlog)}</td>
          <td>{source.quarantineTotal === null ? '-' : displayFormatter.number(source.quarantineTotal)}</td>
          <td class="mono tick">{source.lastTickSummary ?? 'no successful tick yet'}</td>
          {#if canControlNow}
            <td class="actions-col">
              <!-- Both verbs target every quarantined row of the source (the API also takes explicit
                   ids; the card keeps the bulk form). Buttons stay enabled only when there is
                   something quarantined to act on. -->
              <button
                disabled={busy || (source.quarantineTotal ?? 0) === 0}
                onclick={() => (confirming = { verb: 'requeue', jobNamespace: source.jobNamespace, quarantined: source.quarantineTotal ?? 0 })}>
                Requeue
              </button>
              <button
                class="danger-outline"
                disabled={busy || (source.quarantineTotal ?? 0) === 0}
                onclick={() => (confirming = { verb: 'discard', jobNamespace: source.jobNamespace, quarantined: source.quarantineTotal ?? 0 })}>
                Discard
              </button>
            </td>
          {/if}
        </tr>
      {/each}
    </tbody>
  </DataTable>
  {#if message}
    <p class="control-message {messageKind}" role="status">{message}</p>
  {/if}
</div>

{#if confirming}
  {@const target = confirming}
  {#if target.verb === 'requeue'}
    <ConfirmAction
      title="Requeue quarantined outbox rows?"
      body={'All ' + displayFormatter.number(target.quarantined) + ' quarantined row(s) of ' + target.jobNamespace + ' return to pending with a fresh delivery budget; the next relay pass applies it.'}
      confirmLabel="Requeue all"
      onConfirm={(reason) => run('requeue', target.jobNamespace, reason)}
      onCancel={() => (confirming = null)} />
  {:else}
    <ConfirmAction
      title="Discard quarantined outbox rows?"
      body={'All ' + displayFormatter.number(target.quarantined) + ' quarantined row(s) of ' + target.jobNamespace + ' are deleted from the producer\'s staging table. The applied ids stay on the slot job\'s audit trail, but the payloads are gone.'}
      confirmLabel="Discard all"
      danger={true}
      confirmPhrase={target.jobNamespace}
      onConfirm={(reason) => run('discard', target.jobNamespace, reason)}
      onCancel={() => (confirming = null)} />
  {/if}
{/if}

<style>
  .tick {
    font-size: var(--text-sm);
    color: var(--muted);
    max-width: 420px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .actions-col {
    text-align: right;
    white-space: nowrap;
  }
  .control-message {
    margin: 8px 0 0;
    font-size: var(--text-sm);
  }
  .control-message.ok {
    color: var(--ok);
  }
  .control-message.warn {
    color: var(--warn);
  }
  .control-message.bad {
    color: var(--bad);
  }
</style>
