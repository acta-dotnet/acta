<script lang="ts">
  import { payloadRoutes } from '../../api.ts';
  import StoredPayload from './StoredPayload.svelte';

  // The result the job holds, stated by format and size in the job's read and read from its own route by StoredPayload.
  // The page shows the panel only while a result is stored.
  let { jobRef, formatId, byteLength, version }: { jobRef: string; formatId: number; byteLength: number; version: string } =
    $props();
</script>

<section class="detail-panel" aria-label="Job result">
  <h2>Result</h2>
  <StoredPayload path={payloadRoutes.result(jobRef)} {formatId} {byteLength} fileName={jobRef + '-result'} label="result" {version} />
</section>
