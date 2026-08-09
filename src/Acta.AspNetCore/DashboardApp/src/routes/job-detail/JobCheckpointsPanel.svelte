<script lang="ts">
  import { type JobCheckpoint } from '../../api.ts';
  import DataTable from '../../components/DataTable.svelte';
  import PayloadView from '../../components/PayloadView.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StateView from '../../components/StateView.svelte';

  // Presentational: checkpoints (variables, signals, timers, progress, child-latches) come from the
  // aggregate detail read; a value payload expands inline.
  let { checkpoints = [] }: { checkpoints?: JobCheckpoint[] } = $props();
</script>

<section class="detail-panel" aria-label="Job checkpoints">
  <h2>Checkpoints</h2>
  {#if checkpoints.length === 0}
    <StateView emptyText="No checkpoints." />
  {:else}
    <DataTable>
      <caption class="sr-only">Job checkpoints</caption>
      <thead><tr><th>Kind</th><th>Name</th><th>State</th><th>Due</th><th>Value</th></tr></thead>
      <tbody>
        {#each checkpoints as checkpoint (checkpoint.kind + '/' + checkpoint.name)}
          <tr>
            <td>{checkpoint.kind}</td>
            <td class="mono">{checkpoint.name}</td>
            <td>{checkpoint.state ?? '·'}</td>
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
  {/if}
</section>

<style>
  summary { cursor: pointer; color: var(--muted); }
  .checkpoint-value { margin-top: 8px; }
</style>
