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
      <table class="history">
        <caption class="sr-only">Change history</caption>
        <thead><tr><th>When</th><th>Who</th><th>Change</th></tr></thead>
        <tbody>
          {#each history as ev (ev.jobEventId)}
            <tr>
              <td><RelativeTime value={ev.createdAtUtc} /></td>
              <td class="mono">{ev.actorKey ?? ev.actorCode}</td>
              <td>{ev.reasonMessage ?? ev.eventCode}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</section>

<style>
  .history-wrap { overflow-x: auto; }
  table.history { width: 100%; border-collapse: collapse; }
  table.history th {
    text-align: left; padding: 6px 8px; color: var(--muted); font-weight: 600;
    text-transform: uppercase; letter-spacing: 0.04em; font-size: var(--text-sm);
    box-shadow: 0 1px 0 var(--line);
  }
  table.history td { text-align: left; padding: 7px 8px; border-bottom: 1px solid var(--line); vertical-align: middle; }
  table.history tr:last-child td { border-bottom: none; }
</style>
