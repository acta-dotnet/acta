<script lang="ts">
  import { type JobPayloadView } from '../../api.ts';
  import PayloadView from '../../components/PayloadView.svelte';

  // Presentational: the result comes from the aggregate detail read (null until the job produces one).
  let { result = null }: { result?: JobPayloadView | null } = $props();
</script>

<section class="detail-panel" aria-label="Job result">
  <h2>Result</h2>
  {#if result}
    <PayloadView payload={result} />
  {:else}
    <p class="dim">No result yet</p>
  {/if}
</section>
