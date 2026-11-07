<script lang="ts">
  import RelativeTime from '../RelativeTime.svelte';
  import VersionMark from './VersionMark.svelte';

  // One version in a family's strip. Every chip has the same parts in the same columns, its mark, its number and status,
  // then when it was published or saved under the number, so the numbers and the times line up from chip to chip.
  let {
    version,
    status,
    at,
    href,
    shown = false
  }: { version: number; status: string; at: string; href: string; shown?: boolean } = $props();
</script>

<a class="version-chip {status}" class:shown aria-current={shown ? 'true' : undefined} {href} aria-label={`Version ${version}, ${status}`}>
  <VersionMark {status} />
  <span class="version-num">v{version}</span>
  <span class="version-status">{status}</span>
  <span class="version-when"><RelativeTime value={at} /></span>
</a>

<style>
  /* The shown version is the raised chip; the published one's mark carries the accent. A chip is at least as wide as one
     holding the longest status, so a strip of them reads as a row of equal cells. */
  .version-chip {
    display: grid;
    grid-template-columns: 1em auto minmax(0, 1fr);
    grid-template-areas:
      'mark num status'
      '. when when';
    gap: 0 6px;
    align-items: baseline;
    min-width: 9.75em;
    padding: 6px 10px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    color: var(--muted);
    text-decoration: none;
    font-size: var(--text-sm);
  }
  .version-chip > :global(.version-mark) { grid-area: mark; align-self: center; }
  .version-chip:hover { border-color: var(--accent); text-decoration: none; }
  .version-chip.shown { border-color: var(--accent); color: var(--ink); background: var(--nav-active-bg); }
  .version-num { grid-area: num; font-weight: 600; color: var(--ink); font-variant-numeric: tabular-nums; }
  .version-status { grid-area: status; }
  .version-chip.published > :global(.version-mark) { color: var(--ok); }
  .version-chip.draft > :global(.version-mark),
  .version-chip.draft .version-status { color: var(--warn); }
  .version-chip.archived { opacity: 0.6; }
  .version-when { grid-area: when; font-size: var(--text-micro); }
</style>
