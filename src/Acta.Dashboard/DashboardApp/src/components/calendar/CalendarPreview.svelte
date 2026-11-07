<script lang="ts">
  import type { CalendarPreview, CalendarWindowView } from '../../api.ts';
  import { displayFormatter } from '../../format.ts';
  import RelativeTime from '../RelativeTime.svelte';
  import { previewDiff, windowKey } from './calendarDefinition.ts';

  // One version's forecast beside the current version's: an operator sees what publishing would add and
  // remove before deciding. A version that is itself current shows one column of what lies ahead.
  let {
    preview,
    loading = false,
    error = '',
    limit = 10,
    onLimit = () => {},
    onRefresh = () => {}
  }: {
    preview: CalendarPreview | null;
    loading?: boolean;
    error?: string;
    limit?: number;
    onLimit?: (limit: number) => void;
    onRefresh?: () => void;
  } = $props();

  const browserZone = displayFormatter.localTimeZone;
  let zone = $state<'local' | 'utc'>('local');
  let displayZone = $derived(zone === 'utc' ? 'UTC' : browserZone);

  let occurrence = $derived(preview?.role === 'occurrence');
  let isCurrent = $derived(preview !== null && preview.currentVersion === preview.version);
  let hasCurrent = $derived(preview !== null && preview.currentVersion !== null && !isCurrent);

  type Entry = { key: string; startUtc: string; endUtc: string | null };
  const instants = (list: string[]): Entry[] => list.map((at) => ({ key: at, startUtc: at, endUtc: null }));
  const windows = (list: CalendarWindowView[]): Entry[] => list.map((w) => ({ key: windowKey(w), startUtc: w.startUtc, endUtc: w.endUtc }));

  let candidate = $derived(preview ? (occurrence ? instants(preview.occurrencesUtc) : windows(preview.windows)) : []);
  let current = $derived(preview ? (occurrence ? instants(preview.currentOccurrencesUtc) : windows(preview.currentWindows)) : []);
  let diff = $derived(previewDiff(candidate.map((e) => e.key), current.map((e) => e.key)));

  const stamp = (at: string) => displayFormatter.rowTimestampInZone(at, displayZone);
</script>

<div class="preview-tools">
  <span class="preview-title">
    {#if preview === null}
      Forecast
    {:else if isCurrent}
      Version {preview.version} is current: the next {occurrence ? 'occurrences' : 'windows'}
    {:else if hasCurrent}
      Version {preview.version} beside current version {preview.currentVersion}
    {:else}
      Version {preview.version}: the next {occurrence ? 'occurrences' : 'windows'} once published
    {/if}
  </span>
  <label class="preview-zone">
    Show in
    <select bind:value={zone} aria-label="Preview time zone">
      <option value="local">Browser local ({browserZone})</option>
      <option value="utc">UTC</option>
    </select>
  </label>
  <label class="preview-zone">
    Count
    <select value={limit} aria-label="Preview count" onchange={(event) => onLimit(Number(event.currentTarget.value))}>
      <option value={10}>10</option>
      <option value={30}>30</option>
    </select>
  </label>
  <button type="button" onclick={onRefresh} disabled={loading}>Refresh</button>
</div>

{#if loading && preview === null}
  <div class="dim">Loading forecast...</div>
{:else if error}
  <div class="control-message bad" role="status">{error}</div>
{:else if preview}
  {#if hasCurrent}
    <p class="preview-legend">
      <span class="added-mark">{diff.added.size} added</span>
      <span class="removed-mark">{diff.removed.size} removed</span>
      over the next {Math.max(candidate.length, current.length)} {occurrence ? 'occurrences' : 'windows'}.
    </p>
  {/if}
  <div class="preview-columns" class:single={!hasCurrent}>
    {#snippet column(title: string, entries: Entry[], marks: Set<string>, markClass: string, emptyText: string)}
      <div class="preview-column">
        {#if hasCurrent}<div class="preview-column-title">{title}</div>{/if}
        {#if entries.length === 0}
          <div class="dim">{emptyText}</div>
        {:else}
          <ol class="preview-runs">
            {#each entries as entry (entry.key)}
              <li class={marks.has(entry.key) ? markClass : ''}>
                <span class="mono preview-run-time">
                  {stamp(entry.startUtc)}{#if !occurrence}<span class="dim">{' to '}</span>{entry.endUtc ? stamp(entry.endUtc) : 'open'}{/if}
                </span>
                <span class="dim preview-run-relative"><RelativeTime value={entry.startUtc} /></span>
              </li>
            {/each}
          </ol>
        {/if}
      </div>
    {/snippet}
    {@render column(`Version ${preview.version}`, candidate, diff.added, 'added', occurrence ? 'No upcoming occurrences: the calendar is exhausted.' : 'No window ahead: the calendar is closed.')}
    {#if hasCurrent}
      {@render column(`Current version ${preview.currentVersion}`, current, diff.removed, 'removed', 'Nothing ahead on the current version.')}
    {/if}
  </div>
{/if}

<style>
  .preview-tools {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px 16px;
    color: var(--muted);
    font-size: var(--text-sm);
    margin-bottom: 8px;
  }
  .preview-title { flex: 1 1 100%; color: var(--ink); }
  .preview-zone {
    display: inline-flex;
    align-items: center;
    gap: 6px;
  }
  .preview-zone select,
  .preview-tools button {
    padding: 4px 6px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
    font-size: var(--text-sm);
  }
  .preview-legend {
    margin: 0 0 8px;
    font-size: var(--text-sm);
    color: var(--muted);
  }
  .added-mark { color: var(--ok); margin-right: 10px; }
  .removed-mark { color: var(--bad); margin-right: 6px; }
  .preview-columns {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 16px;
    font-size: var(--text-sm);
  }
  .preview-columns.single { grid-template-columns: minmax(0, 1fr); }
  .preview-column-title {
    color: var(--muted);
    font-weight: 600;
    margin-bottom: 4px;
  }
  .preview-runs {
    margin: 0;
    padding: 0;
    list-style: none;
  }
  .preview-runs li {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 4px 12px;
    padding: 4px 6px;
    border-bottom: 1px solid var(--line);
    border-left: 3px solid transparent;
  }
  .preview-runs li:last-child { border-bottom: none; }
  .preview-runs li.added { border-left-color: var(--ok); background: color-mix(in srgb, var(--badge-ok-bg) 60%, transparent); }
  .preview-runs li.removed { border-left-color: var(--bad); background: color-mix(in srgb, var(--badge-bad-bg) 60%, transparent); text-decoration: line-through; text-decoration-color: var(--bad); }
  .preview-run-time { min-width: 19ch; }
  @media (max-width: 760px) {
    .preview-columns { grid-template-columns: minmax(0, 1fr); }
  }
</style>
