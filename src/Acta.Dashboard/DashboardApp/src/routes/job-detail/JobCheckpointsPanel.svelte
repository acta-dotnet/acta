<script lang="ts">
  import { type JobCheckpoint } from '../../api.ts';
  import DataTable from '../../components/DataTable.svelte';
  import PayloadView from '../../components/PayloadView.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StateView from '../../components/StateView.svelte';
  import { displayFormatter } from '../../format.ts';

  // Presentational: checkpoints (variables, signals, timers, progress, child-latches) come from the
  // aggregate detail read's first page; a value payload expands inline, and a larger set says so.
  let { checkpoints = [], total = null }: { checkpoints?: JobCheckpoint[]; total?: number | null } = $props();
</script>

<section class="detail-panel" aria-label="Job checkpoints">
  <h2>Checkpoints</h2>
  {#if checkpoints.length === 0}
    <StateView emptyText="No checkpoints." />
  {:else}
    <DataTable>
      <caption class="sr-only">Job checkpoints</caption>
      <thead><tr><th>Kind</th><th>Name</th><th>Status</th><th>Due</th><th>Value</th></tr></thead>
      <tbody>
        {#each checkpoints as checkpoint (checkpoint.kind + '/' + checkpoint.name)}
          <tr>
            <td>{checkpoint.kind}</td>
            <td class="mono">{checkpoint.name}</td>
            <td>{checkpoint.status ?? '·'}</td>
            <td><RelativeTime value={checkpoint.dueAtUtc} /></td>
            <td>
              {#if checkpoint.value}
                <details>
                  <summary>View</summary>
                  <div class="checkpoint-value"><PayloadView payload={checkpoint.value} /></div>
                </details>
              {:else}
                <span class="dim">·</span>
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
    {#if total !== null && total > checkpoints.length}
      <p class="detail-help">Showing {displayFormatter.number(checkpoints.length)} of {displayFormatter.number(total)}.</p>
    {/if}
  {/if}
</section>

<style>
  summary { cursor: pointer; color: var(--muted); }
  .checkpoint-value { margin-top: 8px; }
</style>
