<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    title,
    breadcrumb,
    actions,
    children
  }: { title: string; breadcrumb?: Snippet; actions?: Snippet; children?: Snippet } = $props();
</script>

{#if breadcrumb}<nav class="page-breadcrumb" aria-label="Breadcrumb">{@render breadcrumb()}</nav>{/if}
<div class="page-head">
  <h1>{title}</h1>
  <div class="spacer"></div>
  {@render actions?.()}
</div>
{@render children?.()}
