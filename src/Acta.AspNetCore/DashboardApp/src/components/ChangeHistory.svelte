<script lang="ts">
  import RelativeTime from './RelativeTime.svelte';
  import type { HistoryEvent } from './changeHistory.ts';

  let {
    history,
    loading = false,
    emptyText = 'No recorded changes.'
  }: { history: HistoryEvent[]; loading?: boolean; emptyText?: string } = $props();
</script>

<section class="detail-panel">
  <h2>Change history</h2>
  {#if loading}
    <p class="detail-help">Loading history...</p>
  {:else if history.length === 0}
    <p class="detail-help">{emptyText}</p>
  {:else}
    <div class="history-wrap">
      <table class="data">
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
      </table>
    </div>
  {/if}
</section>

<style>
  .history-wrap { overflow-x: auto; }
</style>
