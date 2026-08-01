<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    title,
    breadcrumb,
    actions,
    crumbBar = false,
    children
  }: { title: string; breadcrumb?: Snippet; actions?: Snippet; crumbBar?: boolean; children?: Snippet } = $props();
</script>

{#if crumbBar && breadcrumb}
  <!-- Detail pages: one bar carries the trail and the page actions; the entity name inside the
       trail is the page title. -->
  <div class="page-head crumb-bar">
    <nav class="page-breadcrumb" aria-label="Breadcrumb">{@render breadcrumb()}</nav>
    <div class="spacer"></div>
    {@render actions?.()}
  </div>
{:else}
  {#if breadcrumb}<nav class="page-breadcrumb" aria-label="Breadcrumb">{@render breadcrumb()}</nav>{/if}
  <div class="page-head">
    <h1>{title}</h1>
    <div class="spacer"></div>
    {@render actions?.()}
  </div>
{/if}
{@render children?.()}
