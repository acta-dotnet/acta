<script lang="ts">
  import type { JobSnapshot } from './types.ts';
  let { job }: { job: JobSnapshot } = $props();
</script>

<details class="panel raw">
  <summary>Raw snapshot (metadata only; payload bodies are never returned)</summary>
  <pre class="mono">{JSON.stringify(job, null, 2)}</pre>
</details>
