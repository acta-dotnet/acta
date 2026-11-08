<script lang="ts">
  import { type JobPayloadView } from '../../api.ts';
  import PayloadView from '../../components/PayloadView.svelte';

  // Presentational: the result comes from the aggregate detail read (null until the job produces one), and the type
  // its definition declares it returns (null when it returns nothing), which tells "none yet" from "none ever".
  let { result = null, resultTypeName = null }: { result?: JobPayloadView | null; resultTypeName?: string | null } = $props();
</script>

<section class="detail-panel" aria-label="Job result">
  <h2>Result</h2>
  {#if result}
    <PayloadView payload={result} />
  {:else if resultTypeName === null}
    <p class="dim">This job returns no result.</p>
  {:else}
    <p class="dim">No result yet</p>
  {/if}
</section>
