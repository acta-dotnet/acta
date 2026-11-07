<script lang="ts">
  import type { Snippet } from 'svelte';

  // `ledger` opts a table into the shared four-column grid (identity | status-or-detail |
  // namespace | time). Panels that use it line their columns up with each other down the page
  // instead of each table sizing itself.
  let { children, ledger = false }: { children: Snippet; ledger?: boolean } = $props();
</script>

<div class="table-wrap">
  <table class="data" class:ledger>
    {@render children()}
  </table>
</div>
