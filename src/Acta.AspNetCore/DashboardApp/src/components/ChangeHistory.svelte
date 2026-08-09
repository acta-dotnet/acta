<script lang="ts">
  import DataTable from './DataTable.svelte';
  import RelativeTime from './RelativeTime.svelte';
  import StateView from './StateView.svelte';
  import type { HistoryEvent } from './changeHistory.ts';

  let {
    history,
    loading = false,
    emptyText = 'No recorded changes.'
  }: { history: HistoryEvent[]; loading?: boolean; emptyText?: string } = $props();
</script>

<section class="detail-panel">
  <h2>Change history</h2>
  {#if loading || history.length === 0}
    <StateView {loading} loadingText="Loading history..." {emptyText} />
  {:else}
    <DataTable>
      <caption class="sr-only">Change history</caption>
      <thead><tr><th>When</th><th>Who</th><th>Change</th></tr></thead>
      <tbody>
        {#each history as ev (ev.jobEventId)}
          <tr>
            <td><RelativeTime value={ev.createdAtUtc} /></td>
            <td class="mono">{ev.actorKey ?? ev.actorCode}</td>
            <td class="wrap">{ev.reasonMessage ?? ev.eventCode}</td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
  {/if}
</section>
