<script lang="ts">
  import type { Snippet } from 'svelte';
  import FocusDisclosure from './FocusDisclosure.svelte';

  let { children, related, relatedNote, supporting }: {
    children: Snippet;
    related?: Snippet;
    relatedNote?: Snippet;
    supporting?: Snippet;
  } = $props();
</script>

<div class="detail-workspace detail-layout">

  <div class="detail-main">{@render children()}</div>
  {#if supporting}
    <FocusDisclosure title="Metadata" hint="Tags and supporting details">
      <div class="detail-supporting">{@render supporting()}</div>
    </FocusDisclosure>
  {/if}
  {#if related}
    <footer class="detail-related">
      <span>Related</span><nav aria-label="Related links">{@render related()}</nav>
      {#if relatedNote}<div class="detail-related-note">{@render relatedNote()}</div>{/if}
    </footer>
  {/if}
</div>
