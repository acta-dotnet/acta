<script lang="ts">
  import type { DefinitionPerformanceBin, DefinitionPerformanceExecutions } from '../../api.ts';
  import { displayFormatter } from '../../format.ts';
  import { binLabel, histogram, mergeLabels, OUTCOMES, PERCENTILES, type Outcome } from './performanceState.ts';

  let { bins, executions, outcome }: { bins: DefinitionPerformanceBin[]; executions: DefinitionPerformanceExecutions; outcome: Outcome } =
    $props();

  const { number, durationMs, percent } = displayFormatter;
  let chart = $derived(histogram(bins));
  let rules = $derived(PERCENTILES.map(({ name, key }) => ({ name, at: chart.at(executions[key]!) })));
  let labels = $derived(mergeLabels(rules, 7));
  // The bar the pointer is on. With none, the readout names the most common duration.
  let pointed = $state<number | null>(null);
  let hot = $derived(pointed !== null && pointed < chart.bars.length ? pointed : null);
  let shown = $derived(chart.bars[hot ?? chart.modal].bin);
  let sentence = $derived(
    `Histogram of ${outcome} execution durations: ${number(executions.executionCount)} executions from ${durationMs(bins[0].fromMs)} to ` +
      `${durationMs(bins[bins.length - 1].toMs)}, ${PERCENTILES.map(({ name, key }) => `${name} ${durationMs(executions[key]!)}`).join(', ')}. ` +
      'The Values tables below list every bin.'
  );

  function point(event: PointerEvent): void {
    const plot = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const at = ((event.clientX - plot.left) / plot.width) * 100;
    const index = chart.bars.findIndex((bar) => at < bar.left + bar.width);
    pointed = index < 0 ? chart.bars.length - 1 : index;
  }

  // A finger leaves the plot as it lifts, so a tapped bar stays read until the next tap.
  function leave(event: PointerEvent): void {
    if (event.pointerType !== 'touch') pointed = null;
  }
</script>

<figure class="chart">
  <figcaption>
    <h3>Distribution</h3>
    <p class="chart-sub">{OUTCOMES[outcome]} executions by duration, with p50, p95 and p99 marked</p>
  </figcaption>
  <p class="chart-readout">
    <b>{binLabel(shown)}</b>: {number(shown.executionCount)} {shown.executionCount === 1 ? 'execution' : 'executions'},
    {percent(shown.executionCount, executions.executionCount)} of the {outcome}{#if hot === null}<br />The most common duration. Point at a bar to read it.{/if}
  </p>
  <div class="chart-frame histogram">
    <div class="chart-labels" aria-hidden="true">
      {#each labels as label (label.text)}
        <span class="chart-label" class:start={label.at < 15} class:end={label.at > 85} style:left="{label.at}%">{label.text}</span>
      {/each}
    </div>
    <div class="chart-axis y" aria-hidden="true">
      <span class="tick" style:bottom="100%">{number(chart.top)}</span>
      {#if Number.isInteger(chart.top / 2)}<span class="tick" style:bottom="50%">{number(chart.top / 2)}</span>{/if}
      <span class="tick" style:bottom="0">0</span>
    </div>
    <div class="chart-plot" class:pointing={hot !== null} role="img" aria-label={sentence} onpointermove={point} onpointerdown={point} onpointerleave={leave}>
      <div class="chart-gridline" style:bottom="100%"></div>
      <div class="chart-gridline" style:bottom="50%"></div>
      {#each chart.bars as bar, index (bar.bin.fromMs)}
        <div class="chart-bar" class:hot={index === hot} style:left="{bar.left}%" style:width="{bar.width}%">
          {#if bar.bin.executionCount > 0}<span style:height="{bar.height}%"></span>{/if}
        </div>
      {/each}
      {#each rules as rule (rule.name)}<div class="rule {rule.name}" style:left="{rule.at}%"></div>{/each}
    </div>
    <div class="chart-axis x" aria-hidden="true">
      {#each chart.ticks as tick (tick.label)}<span class="tick {tick.edge}" class:alt={tick.alt} style:left="{tick.at}%">{tick.label}</span>{/each}
    </div>
  </div>
</figure>

<style>
  .histogram { grid-template-rows: 20px 190px 24px; grid-template-areas: '. labels' 'y plot' '. x'; }
  /* A label stands centred over its rule, or starts or ends at it near an edge, so it stays over the plot. */
  .chart-label { bottom: 3px; transform: translateX(-50%); }
  .chart-label.start { transform: none; }
  .chart-label.end { transform: translateX(-100%); }
  /* The percentiles stand as ink rules over the bars, p50 the stronger, edged in the panel's colour to stay apart from them. */
  .rule { position: absolute; top: 0; bottom: 0; width: 1px; background: var(--ink); box-shadow: 0 0 0 1px var(--panel); pointer-events: none; }
  .rule.p50 { width: 2px; margin-left: -0.5px; }
  /* A narrow histogram keeps every other duration tick. */
  @container (max-width: 520px) {
    .tick.alt { display: none; }
  }
</style>
