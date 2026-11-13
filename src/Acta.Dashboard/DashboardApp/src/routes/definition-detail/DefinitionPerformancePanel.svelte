<script lang="ts">
  import { createQuery, keepPreviousData } from '@tanstack/svelte-query';
  import { api, sessionExpired, type DefinitionPerformance } from '../../api.ts';
  import { displayFormatter, statusClass } from '../../format.ts';
  import { keys } from '../../query.ts';
  import { now } from '../../time.ts';
  import { createUrlFilters } from '../../urlFilters.ts';
  import DataTable from '../../components/DataTable.svelte';
  import StateView from '../../components/StateView.svelte';
  import DefinitionDurationHistogram from './DefinitionDurationHistogram.svelte';
  import DefinitionPercentileTrend from './DefinitionPercentileTrend.svelte';
  import {
    binLabel,
    instantLabel,
    intervalLabel,
    notReadThrough,
    OUTCOMES,
    PERCENTILES,
    smallSampleNote,
    WINDOWS,
    windowOf,
    windowRange,
    type Outcome,
    type PerformanceWindow
  } from './performanceState.ts';

  let { definitionPath, eventRecording }: { definitionPath: string; eventRecording: string } = $props();

  const id = $props.id();
  const { number, durationMs, percent } = displayFormatter;

  // The window lives in the URL, so a link or a reload opens the same one.
  const filters = createUrlFilters({ window: 'window' }, { window: 'day' });
  let selected = $derived<PerformanceWindow>(Object.hasOwn(WINDOWS, $filters.window) ? ($filters.window as PerformanceWindow) : 'day');

  // The range is taken when the read is made, so a refetch reads up to now. Nothing refetches by itself: the page's
  // refresh does, so the panel is as old as the page says.
  const performance = createQuery(() => ({
    queryKey: keys.detail(`${definitionPath}/performance`, selected),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      api<DefinitionPerformance>(`${definitionPath}/performance`, windowRange(selected, Date.now()), { signal }),
    placeholderData: keepPreviousData,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false
  }));

  export function refresh(): void {
    void performance.refetch();
  }

  let read = $derived(performance.data);
  // A window switch shows the previous read until the next arrives, so the charts name the window of what they show.
  let shownWindow = $derived(read ? windowOf(read) : selected);
  let failuresOnly = $derived(eventRecording === 'failures');
  // The outcome follows the recording until one is chosen: a definition that records failures only shows them first.
  let chosen = $state<Outcome | null>(null);
  let outcome = $derived<Outcome>(chosen ?? (failuresOnly ? 'failed' : 'succeeded'));
  let other = $derived<Outcome>(outcome === 'succeeded' ? 'failed' : 'succeeded');
  let total = $derived(read ? read.succeeded.executionCount + read.failed.executionCount : 0);
  let bins = $derived(read ? (outcome === 'succeeded' ? read.succeededBins : read.failedBins) : []);
  let through = $derived(read ? notReadThrough(read) : -1);
  let sampleNote = $derived(read ? smallSampleNote(read[outcome].executionCount, outcome) : null);
</script>

<div class="perf">
  <h2 class="sr-only">Performance</h2>
  <div class="perf-controls">
    <div class="perf-choice">
      <span class="perf-choice-label" id="{id}-window">Window</span>
      <div class="perf-options" role="radiogroup" aria-labelledby="{id}-window">
        {#each Object.entries(WINDOWS) as [key, { label }] (key)}
          <label><input type="radio" name="{id}-window" value={key} checked={key === selected} onchange={() => filters.patch({ window: key })} /><span>{label}</span></label>
        {/each}
      </div>
    </div>
    <div class="perf-choice">
      <span class="perf-choice-label" id="{id}-outcome">Outcome</span>
      <div class="perf-options" role="radiogroup" aria-labelledby="{id}-outcome">
        {#each Object.entries(OUTCOMES) as [value, label] (value)}
          <label><input type="radio" name="{id}-outcome" {value} checked={value === outcome} onchange={() => (chosen = value as Outcome)} /><span>{label}</span></label>
        {/each}
      </div>
    </div>
  </div>

  {#if read}
    <div class="perf-read" class:stale={performance.isFetching}>
      {#if performance.error && !$sessionExpired}
        <p class="control-message warn">The latest read failed, so these figures are from the one before it: {performance.error.message}</p>
      {/if}
      {#if total > 0}
        <dl class="perf-facts">
          <div>
            <dt>{failuresOnly ? 'Recorded executions' : 'Executions'}</dt>
            <dd>{number(total)}</dd>
            {#if read.executionsHasMore}<dd class="fact-note">the newest in this window</dd>{/if}
          </div>
          {#if failuresOnly}
            <div><dt>Failed</dt><dd>{number(read.failed.executionCount)}</dd></div>
            <div><dt>Succeeded</dt><dd>{number(read.succeeded.executionCount)}</dd></div>
          {:else}
            <div>
              <dt>Failure rate</dt>
              <dd>{percent(read.failed.executionCount, total)}</dd>
              <dd class="fact-note">{number(read.failed.executionCount)} failed</dd>
            </div>
          {/if}
          {#if read[outcome].executionCount > 0}
            {#each PERCENTILES as { name, key } (name)}
              <div><dt>{OUTCOMES[outcome]} {name}</dt><dd>{durationMs(read[outcome][key]!)}</dd></div>
            {/each}
          {/if}
        </dl>
      {/if}
      {#if failuresOnly || read.executionsHasMore || sampleNote}
        <div class="perf-notes">
          {#if failuresOnly}
            <p class="perf-note">
              <strong>This definition records failures only.</strong> A success is recorded only when it follows a failure or
              closes an alert, so the succeeded durations are a partial set and no failure rate is shown.
            </p>
          {/if}
          {#if read.executionsHasMore}
            <p class="perf-note">
              <strong>Showing the newest {number(total)} executions, back to {instantLabel(read.oldestAtUtc!, shownWindow)}.</strong>
              Older executions in this window were not read.{#if shownWindow !== 'hour'}{' '}A shorter window may show all of its executions.{/if}
            </p>
          {/if}
          {#if sampleNote}<p class="perf-note">{sampleNote}</p>{/if}
        </div>
      {/if}

      {#if read[outcome].executionCount === 0}
        <p class="perf-empty">{total === 0 ? 'No finished executions in this window.' : `No ${outcome} executions in this window.`}</p>
      {:else}
        <div class="perf-charts t-{statusClass(outcome)}">
          <DefinitionDurationHistogram {bins} executions={read[outcome]} {outcome} />
          <DefinitionPercentileTrend {read} {outcome} window={shownWindow} />
        </div>
        <details class="perf-values">
          <summary>Values <span class="dim">· {displayFormatter.zoneNote($now)}</span></summary>
          <div class="perf-values-body">
            <section>
              <h4>Distribution</h4>
              <DataTable>
                <caption class="sr-only">{OUTCOMES[outcome]} executions per duration bin</caption>
                <thead><tr><th>Duration</th><th class="col-num">Executions</th><th class="col-num">Share</th></tr></thead>
                <tbody>
                  {#each bins as bin (bin.fromMs)}
                    <tr>
                      <td>{binLabel(bin)}</td>
                      <td class="col-num">{number(bin.executionCount)}</td>
                      <td class="col-num">{percent(bin.executionCount, read[outcome].executionCount)}</td>
                    </tr>
                  {/each}
                </tbody>
              </DataTable>
            </section>
            <section>
              <h4>Over time</h4>
              <DataTable>
                <caption class="sr-only">{OUTCOMES[outcome]} execution durations per interval</caption>
                <thead>
                  <tr>
                    <th>Interval</th>
                    {#each PERCENTILES as { name } (name)}<th class="col-num">{name}</th>{/each}
                    <th class="col-num">{OUTCOMES[outcome]}</th>
                    <th class="col-num">{OUTCOMES[other]}</th>
                  </tr>
                </thead>
                <tbody>
                  {#each read.intervals as interval, index (interval.startAtUtc)}
                    <tr>
                      <td>{intervalLabel(interval.startAtUtc, shownWindow)}</td>
                      {#if index <= through}
                        <td class="dim" colspan="5">Not read</td>
                      {:else}
                        {#each PERCENTILES as { name, key } (name)}
                          <td class="col-num">{interval[outcome][key] === null ? '·' : durationMs(interval[outcome][key]!)}</td>
                        {/each}
                        <td class="col-num">{number(interval[outcome].executionCount)}</td>
                        <td class="col-num">{number(interval[other].executionCount)}</td>
                      {/if}
                    </tr>
                  {/each}
                </tbody>
              </DataTable>
            </section>
          </div>
        </details>
      {/if}
      <p class="detail-help perf-caption">
        Counts recorded executions that ended in success or failure, from retained events, each timed by its worker from
        reading the input to the handler's return. Durations are truncated to two significant figures.
      </p>
    </div>
  {:else}
    <StateView
      loading={performance.isPending}
      error={performance.error?.message ?? null}
      loadingText="Loading performance..."
      onRetry={() => performance.refetch()} />
  {/if}
</div>

<style>
  .perf, .perf-read { display: grid; gap: 18px; min-width: 0; }
  /* A refetch holds the previous read in view, set back until the next one arrives. */
  .perf-read.stale { opacity: 0.6; }
  .perf-controls { display: flex; flex-wrap: wrap; gap: 10px 28px; }

  /* A choice among a few views: native radios drawn as one joined control. The chosen one carries weight, a tint and
     an underline, so the choice never rests on colour alone. */
  .perf-choice { display: flex; align-items: center; flex-wrap: wrap; gap: 6px 10px; min-width: 0; }
  .perf-choice-label, .perf-facts dt { color: var(--muted); font-size: var(--text-xs); font-weight: 500; }
  .perf-options { display: inline-flex; flex-wrap: wrap; border: 1px solid var(--line); border-radius: var(--radius-control); background: var(--panel); overflow: hidden; }
  .perf-options label { position: relative; display: block; }
  .perf-options label + label span { border-left: 1px solid var(--line); }
  .perf-options input { position: absolute; inset: 0; width: 100%; height: 100%; margin: 0; opacity: 0; cursor: pointer; }
  .perf-options span { display: block; padding: 5px 13px; color: var(--muted); font-size: var(--text-sm); white-space: nowrap; }
  .perf-options input:hover + span { color: var(--ink); }
  .perf-options input:checked + span {
    color: var(--ink); font-weight: 600;
    background: color-mix(in srgb, var(--accent) 14%, var(--panel)); box-shadow: inset 0 -2px 0 var(--accent);
  }
  .perf-options input:focus-visible + span { outline: 2px solid var(--accent); outline-offset: -2px; }

  .perf-facts { display: flex; flex-wrap: wrap; gap: 14px 44px; margin: 0; }
  .perf-facts > div { min-width: 0; }
  .perf-facts dd { margin: 0; font-size: calc(var(--text-metric-value) * 0.8); font-weight: 600; letter-spacing: -0.015em; line-height: 1.3; }
  .perf-facts dd.fact-note { color: var(--muted); font-size: var(--text-supporting); font-weight: var(--weight-text); letter-spacing: 0; }

  .perf-notes { display: grid; gap: 8px; }
  .perf-note { margin: 0; padding: 9px 12px; border-radius: var(--radius-control); background: var(--panel-subtle); font-size: var(--text-sm); max-width: 92ch; }
  .perf-note strong { font-weight: 600; }
  .perf-empty { margin: 0; padding: 28px 0; color: var(--muted); font-size: var(--text-md); }

  /* Both charts draw the shown outcome in its tone, set once here. */
  .perf-charts { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.3fr); gap: 20px 32px; align-items: start; }
  .perf-values { border-top: 1px solid var(--line); padding-top: 12px; }
  .perf-values > summary { width: fit-content; cursor: pointer; font-size: var(--text-md); font-weight: 500; }
  .perf-values > summary:hover { color: var(--accent); }
  .perf-values-body { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.5fr); gap: 20px 32px; padding-top: 14px; }
  .perf-values-body section { min-width: 0; }
  .perf-values-body h4 { margin: 0 0 6px; font-size: var(--text-sm); font-weight: 600; }
  /* The tables run their full length: a box that scrolled them inside would need a tab stop of its own to be read by
     keyboard, where the page's own scroll needs none. */
  .perf-values-body :global(.table-wrap) { max-height: none; }
  @container (max-width: 900px) {
    .perf-charts, .perf-values-body { grid-template-columns: minmax(0, 1fr); }
  }
  .perf-caption { margin: 0; max-width: 78ch; }
</style>
