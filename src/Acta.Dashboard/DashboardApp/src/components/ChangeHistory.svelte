<script lang="ts">
  import DataTable from './DataTable.svelte';
  import RelativeTime from './RelativeTime.svelte';
  import StateView from './StateView.svelte';
  import type { HistoryEvent } from './changeHistory.ts';
  import { eventFacts } from '../lib/eventFacts.ts';

  let {
    history,
    loading = false,
    emptyText = 'No recorded changes.'
  }: { history: HistoryEvent[]; loading?: boolean; emptyText?: string } = $props();

  // Only an operator verb records who acted (details.actor); a system or worker event names no one, so
  // the column shows only when some row has an actor.
  let actors = $derived(history.map((ev) => eventFacts(ev.detailsText).actor ?? null));
  let showWho = $derived(actors.some((actor) => actor !== null));
</script>

<section class="detail-panel">
  <h2>Change history</h2>
  {#if loading || history.length === 0}
    <StateView {loading} loadingText="Loading history..." {emptyText} />
  {:else}
    <DataTable>
      <caption class="sr-only">Change history</caption>
      <thead><tr><th>When</th>{#if showWho}<th>Who</th>{/if}<th>Change</th></tr></thead>
      <tbody>
        {#each history as ev, index (ev.eventId)}
          <tr>
            <td><RelativeTime value={ev.createdAtUtc} /></td>
            {#if showWho}<td class="mono">{actors[index] ?? '·'}</td>{/if}
            <td class="wrap">{ev.message ?? ev.eventCode}</td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
  {/if}
</section>
