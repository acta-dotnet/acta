<script lang="ts">
  import type { JobSnapshot } from './types.ts';
  let { job }: { job: JobSnapshot } = $props();
</script>

<details class="panel raw">
  <summary>Raw snapshot (the job's metadata record)</summary>
  <pre class="mono">{JSON.stringify(job, null, 2)}</pre>
</details>
