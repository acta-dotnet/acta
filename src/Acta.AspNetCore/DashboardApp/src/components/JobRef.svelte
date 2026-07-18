<script lang="ts">
  let { value, href = null }: { value: string; href?: string | null } = $props();
  // Public refs are a type tag ('job_') + 26 Crockford-base32 ULID chars. The first 10 id chars encode the
  // creation millisecond, so rows created together share them; only the entropy tail differs.
  const match = $derived(/^([a-z0-9]+_)([0-9a-hjkmnp-tv-z]{10})([0-9a-hjkmnp-tv-z]{16})$/i.exec(value ?? ''));
</script>

{#snippet parts()}
  {#if match}<span class="ref-head">{match[1]}{match[2]}</span><span class="ref-tail">{match[3]}</span>{:else}{value}{/if}
{/snippet}

{#if href}
  <a class="mono jobref" {href}>{@render parts()}</a>
{:else}
  <span class="mono jobref">{@render parts()}</span>
{/if}

<style>
  .jobref { white-space: nowrap; }
  .ref-head { color: var(--muted); }
</style>
