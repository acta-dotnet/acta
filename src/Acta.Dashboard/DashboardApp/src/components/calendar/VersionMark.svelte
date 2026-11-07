<script lang="ts">
  import Icon from '../Icon.svelte';

  // One calendar version's status as a mark in the surrounding text's colour: a check for the published version, a
  // minus for an archived one, a dot for a draft or a superseded one. Every mark takes the same square, so whatever
  // follows it starts at the same place.
  let { status }: { status: string } = $props();

  const ICONS: Record<string, string> = { published: 'check-circle', archived: 'minus-circle' };
  let icon = $derived(ICONS[status] ?? null);
</script>

<span class="version-mark" aria-hidden="true">
  {#if icon}<Icon name={icon} />{:else}<i class="version-dot"></i>{/if}
</span>

<style>
  .version-mark {
    display: inline-flex;
    flex: none;
    align-items: center;
    justify-content: center;
    width: 1em;
    height: 1em;
  }
  /* The icon's own optical nudge is for a glyph beside a baseline; inside this square the square does the centring. */
  .version-mark :global(.ic) { top: 0; width: 0.92em; height: 0.92em; }
  .version-dot {
    width: 0.6em;
    height: 0.6em;
    border-radius: 50%;
    background: currentColor;
  }
</style>
