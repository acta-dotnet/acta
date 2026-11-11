<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import { capabilitiesQuery, can, Permission } from '../query.ts';
  import { displayFormatter } from '../format.ts';
  import { outboxControlMessage } from '../lib/controlMessages.ts';
  import ConfirmAction from './ConfirmAction.svelte';
  import DataTable from './DataTable.svelte';
  import RelativeTime from './RelativeTime.svelte';

  // One relay source line from outbox/sources: what the sys.outbox slot's last successful tick left.
  // backlog, quarantineTotal and lastTickAtUtc are null before that first tick, never zero-faked.
  interface OutboxSourceView {
    jobNamespace: string;
    jobRef: string;
    lastTickAtUtc: string | null;
    backlog: number | null;
    quarantineTotal: number | null;
    isLocal: boolean;
  }

  interface OutboxControlResponse {
    action: string;
    pendingSinceUtc: string | null;
  }

  let {
    sources = [],
    onChanged = () => {}
  }: {
    sources?: OutboxSourceView[];
    onChanged?: () => void;
  } = $props();

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(can(capabilities.data, Permission.control));
  let canAdminNow = $derived(can(capabilities.data, Permission.admin));
  // Requeue needs control and discard admin; the column shows for either.
  let canActNow = $derived(canControlNow || canAdminNow);

  let message = $state('');
  let messageKind = $state('');
  let confirming = $state<{ verb: 'requeue' | 'discard'; jobNamespace: string; quarantined: number } | null>(null);

  // Requeue/discard park a durable command on the source's relay slot; 202 accepted means "the next
  // relay pass applies it", so success invalidates the overview snapshot rather than expecting the
  // counters to have moved already. A requeue without ids takes every quarantined row; a discard
  // must say so with all.
  const mutation = useControlMutation<{ verb: 'requeue' | 'discard'; jobNamespace: string; reason?: string }, OutboxControlResponse>({
    path: (vars) => `outbox/${vars.jobNamespace}/${vars.verb}`,
    body: (vars) => (vars.verb === 'discard' ? { all: true } : {}),
    notFound: () => ({ action: 'not-found', pendingSinceUtc: null }),
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
          ? outboxControlMessage(verb, result.action) + ' Pending since ' + displayFormatter.timestamp(result.pendingSinceUtc) + '.'
          : outboxControlMessage(verb, result.action);
      if (result.action === 'accepted') onChanged();
    } catch (e) {
      message = (e as Error).message;
      messageKind = 'bad';
    }
  }
</script>

<div class="panel">
  <h2>Outbox relays</h2>
  <!-- The shared four-column grid has no width for a fifth column, so with the Actions column the table sizes itself. -->
  <DataTable ledger={!canActNow}>
    <caption class="sr-only">Outbox relay sources</caption>
    <thead>
      <tr>
        <th>Namespace</th>
        <th>Backlog</th>
        <th>Quarantined</th>
        <th>Last tick</th>
        {#if canActNow}<th class="actions-col">Actions</th>{/if}
      </tr>
    </thead>
    <tbody>
      {#each sources as source}
        <tr class:trouble={(source.quarantineTotal ?? 0) > 0}>
          <td>{source.jobNamespace}</td>
          <td>{source.backlog === null ? '-' : displayFormatter.number(source.backlog)}</td>
          <td>{source.quarantineTotal === null ? '-' : displayFormatter.number(source.quarantineTotal)}</td>
          <td class="tick">{#if source.lastTickAtUtc}<RelativeTime value={source.lastTickAtUtc} />{:else}<span class="dim">no successful tick yet</span>{/if}</td>
          {#if canActNow}
            <td class="actions-col">
              <!-- Both verbs target every quarantined row of the source (the API also takes explicit
                   ids; the card keeps the bulk form). Buttons stay enabled only when there is
                   something quarantined to act on. -->
              {#if canControlNow}
                <button
                  disabled={busy || (source.quarantineTotal ?? 0) === 0}
                  onclick={() => (confirming = { verb: 'requeue', jobNamespace: source.jobNamespace, quarantined: source.quarantineTotal ?? 0 })}>
                  Requeue
                </button>
              {/if}
              {#if canAdminNow}
                <button
                  class="danger-outline"
                  disabled={busy || (source.quarantineTotal ?? 0) === 0}
                  onclick={() => (confirming = { verb: 'discard', jobNamespace: source.jobNamespace, quarantined: source.quarantineTotal ?? 0 })}>
                  Discard
                </button>
              {/if}
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
      body={'All ' + displayFormatter.number(target.quarantined) + ' quarantined row(s) of ' + target.jobNamespace + ' are deleted from the producer\'s staging table. The applied ids stay on the relay job\'s audit trail, but the payloads are gone.'}
      confirmLabel="Discard all"
      danger={true}
      confirmPhrase={target.jobNamespace}
      onConfirm={(reason) => run('discard', target.jobNamespace, reason)}
      onCancel={() => (confirming = null)} />
  {/if}
{/if}

<style>
  /* The one column that takes what the others leave, so the Actions column sits at the panel's edge at any width. */
  .tick {
    width: 100%;
  }
  .actions-col {
    text-align: right;
    white-space: nowrap;
  }
  .control-message {
    margin: 8px 0 0;
    font-size: var(--text-sm);
  }
</style>
