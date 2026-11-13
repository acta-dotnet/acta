<script lang="ts">
  import type { DefinitionPerformance } from '../../api.ts';
  import { displayFormatter } from '../../format.ts';
  import {
    axisLabel,
    countCeiling,
    intervalLabel,
    mergeLabels,
    notReadThrough,
    OUTCOMES,
    PERCENTILES,
    rangeMark,
    trendForm,
    trendLines,
    trendPoints,
    trendScale,
    WINDOWS,
    type Outcome,
    type PerformanceWindow
  } from './performanceState.ts';

  let { read, outcome, window }: { read: DefinitionPerformance; outcome: Outcome; window: PerformanceWindow } = $props();

  const { number, durationMs } = displayFormatter;
  let other = $derived<Outcome>(outcome === 'succeeded' ? 'failed' : 'succeeded');
  let count = $derived(read.intervals.length);
  let through = $derived(notReadThrough(read));
  let points = $derived(trendPoints(read, outcome));
  let form = $derived(trendForm(read[outcome].executionCount));
  let scale = $derived(trendScale(points, read[outcome]));
  let lines = $derived(form === 'lines' ? trendLines(points, scale.at) : null);
  let countTop = $derived(countCeiling(Math.max(0, ...points.map((point) => point?.executionCount ?? 0))));
  // The latest interval with executions: the readout names it when nothing is pointed at, and the lines end there.
  let latest = $derived(points.reduce((last, point, index) => (point ? index : last), -1));
  let ends = $derived(
    form === 'lines' && latest >= 0 ? mergeLabels(PERCENTILES.map(({ name, key }) => ({ name, at: scale.at(points[latest]![key]!) })), 9) : []
  );
  let pointed = $state<number | null>(null);
  let hot = $derived(pointed !== null && pointed < count ? pointed : null);
  let shown = $derived(hot ?? (latest >= 0 ? latest : count - 1));
  let interval = $derived(read.intervals[shown]);
  let step = $derived(read.intervalSeconds < 3600 ? `${read.intervalSeconds / 60} min` : `${read.intervalSeconds / 3600} h`);
  let sentence = $derived(
    `Trend of ${outcome} execution durations over the last ${window} in ${count} intervals: ` +
      `${PERCENTILES.map(({ name, key }) => `${name} ${durationMs(read[outcome][key]!)}`).join(', ')} over the window` +
      (latest >= 0 ? `, and p50 ${durationMs(points[latest]!.p50Ms!)}, p99 ${durationMs(points[latest]!.p99Ms!)} in the latest interval` : '') +
      '. The Values tables below list every interval.'
  );

  const middle = (index: number) => ((index + 0.5) / count) * 100;

  function point(event: PointerEvent): void {
    const plot = (event.currentTarget as HTMLElement).getBoundingClientRect();
    pointed = Math.min(count - 1, Math.max(0, Math.floor(((event.clientX - plot.left) / plot.width) * count)));
  }

  // A finger leaves the plot as it lifts, so a tapped interval stays read until the next tap.
  function leave(event: PointerEvent): void {
    if (event.pointerType !== 'touch') pointed = null;
  }
</script>

<figure class="chart">
  <figcaption>
    <h3>Over time</h3>
    <p class="chart-sub">{OUTCOMES[outcome]} execution duration per {step}</p>
  </figcaption>
  <ul class="legend">
    {#if form === 'lines'}
      <li><span class="key"></span>p50</li>
      <li><span class="key p95"></span>p95</li>
      <li><span class="key p99"></span>p99</li>
    {:else}
      <li><span class="key dot"></span>p50</li>
      <li><span class="key mark"></span>p95 and, above it, p99</li>
    {/if}
    <li><span class="key count"></span>executions</li>
  </ul>
  <p class="chart-readout">
    <b>{intervalLabel(interval.startAtUtc, window)}</b>:
    {#if shown <= through}
      not read
    {:else if interval[outcome].executionCount === 0}
      no {outcome} executions{#if interval[other].executionCount > 0}, {number(interval[other].executionCount)} {other}{/if}
    {:else}
      p50 <b>{durationMs(interval[outcome].p50Ms!)}</b>, p95 <b>{durationMs(interval[outcome].p95Ms!)}</b>, p99
      <b>{durationMs(interval[outcome].p99Ms!)}</b><br />{number(interval[outcome].executionCount)} {outcome},
      {number(interval[other].executionCount)} {other}{/if}{#if hot === null}{#if shown <= through || interval[outcome].executionCount === 0}<br />{:else}{'. '}{/if}The
      latest interval. Point at the chart to read another.{/if}
  </p>
  <!-- The first row keeps the top duration tick clear of the readout, as the histogram's label row does. -->
  <div class="chart-frame trend">
    <div class="chart-axis y" aria-hidden="true">
      {#each scale.ticks as tick (tick.label)}<span class="tick" style:bottom="{tick.at}%">{tick.label}</span>{/each}
    </div>
    <div class="chart-plot" role="img" aria-label={sentence} onpointermove={point} onpointerdown={point} onpointerleave={leave}>
      {#each scale.ticks as tick (tick.label)}<div class="chart-gridline" style:bottom="{tick.at}%"></div>{/each}
      {#if through >= 0}<div class="not-read" style:width="{((through + 1) / count) * 100}%"><span>Not read</span></div>{/if}
      {#if lines}
        <svg viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
          <path class="band" d={lines.band} />
          <path class="line p99" d={lines.p99} />
          <path class="line p95" d={lines.p95} />
          <path class="line p50" d={lines.p50} />
        </svg>
        {#each lines.dots as index (index)}
          {#each PERCENTILES as { name, key } (name)}<span class="point {name}" style:left="{middle(index)}%" style:bottom="{scale.at(points[index]![key]!)}%"></span>{/each}
        {/each}
      {:else}
        {#each points as point, index (index)}
          {#if point}
            {@const mark = rangeMark(point, scale.at)}
            {#if mark.p99 !== null}
              <span class="mark-stem" style:left="{middle(index)}%" style:bottom="{mark.p50}%" style:height="{mark.p99 - mark.p50}%"></span>
              <span class="mark-tick" style:left="{middle(index)}%" style:bottom="{mark.p99}%" style:width="{70 / count}%"></span>
            {/if}
            {#if mark.p95 !== null}<span class="mark-tick" style:left="{middle(index)}%" style:bottom="{mark.p95}%" style:width="{70 / count}%"></span>{/if}
            <span class="mark-dot" style:left="{middle(index)}%" style:bottom="{mark.p50}%" style:width="{85 / count}%"></span>
          {/if}
        {/each}
      {/if}
      {#if hot !== null}<div class="cross" style:left="{middle(hot)}%"></div>{/if}
    </div>
    <div class="ends" aria-hidden="true">
      {#each ends as label (label.text)}<span class="chart-label" style:bottom="{Math.min(97, Math.max(3, label.at))}%">{label.text}</span>{/each}
    </div>
    <div class="chart-axis count" aria-hidden="true"><span class="tick">{number(countTop)}</span></div>
    <div class="strip t-neutral" class:pointing={hot !== null} aria-hidden="true" onpointermove={point} onpointerdown={point} onpointerleave={leave}>
      {#if through >= 0}<div class="not-read" style:width="{((through + 1) / count) * 100}%"></div>{/if}
      {#each points as point, index (index)}
        <div class="chart-bar" class:hot={index === hot} style:left="{(index / count) * 100}%" style:width="{100 / count}%">
          {#if point}<span style:height="{(point.executionCount / countTop) * 100}%"></span>{/if}
        </div>
      {/each}
    </div>
    <div class="chart-axis x" aria-hidden="true">
      {#each read.intervals as { startAtUtc }, index (startAtUtc)}
        {#if index % WINDOWS[window].tickEvery === 0}
          <span class="tick" class:first={index === 0} style:left="{(index / count) * 100}%">{axisLabel(startAtUtc, window)}</span>
        {/if}
      {/each}
    </div>
  </div>
</figure>

<style>
  .trend {
    grid-template-columns: var(--axis-width) minmax(0, 1fr) 42px;
    grid-template-rows: 20px 190px 50px 24px;
    grid-template-areas: '. labels .' 'y plot ends' 'count strip .' '. x .';
  }
  .legend { display: flex; flex-wrap: wrap; gap: 4px 16px; margin: 0; padding: 0; list-style: none; color: var(--muted); font-size: var(--text-micro); }
  .legend li { display: inline-flex; align-items: center; gap: 6px; }
  .key { display: inline-block; width: 18px; border-top: 2px solid var(--tone); }
  .key.p95 { border-top: 1.5px dashed var(--tone); }
  .key.p99 { border-top-width: 1px; }
  .key.dot { width: 7px; height: 7px; border: 0; border-radius: 50%; background: var(--tone); }
  .key.mark { width: 8px; }
  .key.count { width: 8px; height: 10px; border: 0; border-radius: 2px 2px 0 0; background: var(--muted); }

  /* p50 is the strong line; p95 dashed and p99 fine above it, with a wash from p50 to p99, so a tail that climbs while
     the median holds shows as the band widening. */
  svg { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; pointer-events: none; }
  .band { fill: var(--tone); fill-opacity: 0.13; }
  .line { fill: none; stroke: var(--tone); vector-effect: non-scaling-stroke; stroke-linejoin: round; stroke-linecap: round; }
  .line.p50 { stroke-width: 2.25px; }
  .line.p95 { stroke-width: 1.5px; stroke-dasharray: 5 4; }
  .line.p99 { stroke-width: 1.25px; }
  .point { position: absolute; width: 8px; height: 8px; margin: -4px; border-radius: 50%; background: var(--tone); box-shadow: 0 0 0 2px var(--panel); pointer-events: none; }
  .point.p95, .point.p99 { width: 6px; height: 6px; margin: -3px; }

  /* A range mark: the dot and the ticks carry the values, the pale stem only joins them. Their width is a share of
     the interval's slot, so a narrow plot keeps neighbours apart. */
  .mark-stem { position: absolute; width: 3px; margin-left: -1.5px; background: color-mix(in srgb, var(--tone) 34%, var(--panel)); pointer-events: none; }
  .mark-tick { position: absolute; max-width: 7px; height: 2px; margin-bottom: -1px; transform: translateX(-50%); background: var(--tone); pointer-events: none; }
  .mark-dot { position: absolute; max-width: 7px; aspect-ratio: 1; border-radius: 50%; transform: translate(-50%, 50%); background: var(--tone); box-shadow: 0 0 0 1px var(--panel); pointer-events: none; }

  .cross { position: absolute; top: 0; bottom: 0; border-left: 1px solid var(--muted); pointer-events: none; }
  .not-read {
    position: absolute; top: 0; bottom: 0; left: 0;
    display: flex; align-items: center; justify-content: center; overflow: hidden; pointer-events: none;
    background: repeating-linear-gradient(135deg, transparent 0 7px, color-mix(in srgb, var(--muted) 22%, transparent) 7px 8px), var(--panel-subtle);
  }
  .not-read span { padding: 2px 8px; border-radius: 999px; background: var(--panel); color: var(--muted); font-size: var(--text-micro); white-space: nowrap; }

  .ends { grid-area: ends; position: relative; }
  /* Names that meet at the line ends stack in their column rather than run past the chart. */
  .ends .chart-label { left: 8px; right: 0; white-space: normal; transform: translateY(50%); }
  .count { grid-area: count; }
  .count .tick { right: 8px; top: 18px; }
  /* The executions share the time axis on a scale of their own, below clear space, so their one tick is never read as
     a duration. */
  .strip { grid-area: strip; position: relative; margin-top: 18px; border-bottom: 1px solid var(--muted); cursor: crosshair; touch-action: pan-y; }</style>
