<script lang="ts">
  import Icon from '../Icon.svelte';
  import {
    WEEKDAYS,
    openMinutes,
    windowProblem,
    windowSpans,
    type DateOverride,
    type ExcludedRange,
    type LocalWindow,
    type WeeklyModel,
    type Weekday
  } from './calendarDefinition.ts';

  // The weekly-availability editor: seven 24-hour bars an operator reads at a glance, the windows under
  // each as time pairs, then the dates that override a weekday and the ranges subtracted last. Every
  // change hands a new model up; the page turns it back into definition text, so the JSON editor and
  // this one always show the same definition.
  let {
    model,
    readOnly = false,
    onChange = () => {}
  }: { model: WeeklyModel; readOnly?: boolean; onChange?: (model: WeeklyModel) => void } = $props();

  const HOURS = [0, 6, 12, 18, 24];

  function emit(patch: Partial<WeeklyModel>) {
    onChange({ ...model, ...patch });
  }

  function setDay(day: Weekday, windows: LocalWindow[]) {
    emit({ days: { ...model.days, [day]: windows } });
  }

  function setOverride(index: number, override: DateOverride) {
    emit({ dateOverrides: model.dateOverrides.map((item, i) => (i === index ? override : item)) });
  }

  function setRange(index: number, range: ExcludedRange) {
    emit({ excludedRanges: model.excludedRanges.map((item, i) => (i === index ? range : item)) });
  }

  // A new window starts where the day's last one ends, so stacking afternoon after morning takes one click.
  function nextWindow(windows: LocalWindow[]): LocalWindow {
    const last = windows[windows.length - 1];
    return last ? { from: last.to, to: last.to < '23:00' ? '23:00' : last.to } : { from: '09:00', to: '17:00' };
  }

  function hoursText(windows: LocalWindow[]): string {
    const minutes = openMinutes(windows);
    if (minutes === 0) return 'closed';
    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    return rest === 0 ? `${hours}h` : `${hours}h ${rest}m`;
  }

  function dateRange(range: ExcludedRange): { from: string; to: string } | null {
    return 'from' in range ? range : null;
  }
</script>

{#snippet windowsEditor(windows: LocalWindow[], label: string, set: (windows: LocalWindow[]) => void)}
  <div class="windows">
    {#each windows as window, index (index)}
      {@const problem = windowProblem(window)}
      <span class="window-chip" class:invalid={problem !== null} title={problem ?? undefined}>
        <input
          type="time"
          value={window.from}
          disabled={readOnly}
          aria-label={`${label} window ${index + 1} from`}
          onchange={(event) => set(windows.map((w, i) => (i === index ? { ...w, from: event.currentTarget.value } : w)))} />
        <span class="window-to">to</span>
        <input
          type="time"
          value={window.to}
          disabled={readOnly}
          aria-label={`${label} window ${index + 1} to`}
          onchange={(event) => set(windows.map((w, i) => (i === index ? { ...w, to: event.currentTarget.value } : w)))} />
        {#if !readOnly}
          <button type="button" class="iconly chip-remove" aria-label={`Remove ${label} window ${index + 1}`} onclick={() => set(windows.filter((_, i) => i !== index))}>
            <Icon name="x" />
          </button>
        {/if}
      </span>
    {/each}
    {#if !readOnly}
      <button type="button" class="window-add" onclick={() => set([...windows, nextWindow(windows)])}><Icon name="plus" />Window</button>
    {/if}
  </div>
{/snippet}

<div class="week" role="group" aria-label="Weekly hours">
  <div class="week-scale" aria-hidden="true">
    <span class="week-day"></span>
    <span class="week-ticks">
      {#each HOURS as hour}<span class="tick" style:left={`${(hour / 24) * 100}%`}>{String(hour).padStart(2, '0')}</span>{/each}
    </span>
  </div>
  {#each WEEKDAYS as day (day)}
    {@const windows = model.days[day]}
    <div class="week-row" class:closed={windows.length === 0}>
      <span class="week-day">{day.slice(0, 3)}</span>
      <div class="week-bar" aria-label={`${day}: ${hoursText(windows)}`}>
        {#each windows as window, index (index)}
          {#each windowSpans(window) as span}
            <span class="week-span" style:left={`${span.left}%`} style:width={`${span.width}%`}></span>
          {/each}
        {/each}
        <span class="week-hours">{hoursText(windows)}</span>
      </div>
      <div class="week-windows">
        {@render windowsEditor(windows, day, (next) => setDay(day, next))}
      </div>
    </div>
  {/each}
</div>

<div class="weekly-fields">
  <label class="detail-field">
    Time zone
    <input
      type="text"
      value={model.timeZone}
      disabled={readOnly}
      placeholder="schedule"
      list="weekly-zone-hints"
      onchange={(event) => emit({ timeZone: event.currentTarget.value.trim() || 'schedule' })} />
    <datalist id="weekly-zone-hints"><option value="schedule"></option><option value="UTC"></option></datalist>
    <span class="field-help">"schedule" takes each bound schedule's own zone; a zone id such as Europe/Ljubljana fixes it.</span>
  </label>
  <label class="detail-field">
    Description
    <input type="text" value={model.description} disabled={readOnly} maxlength="512" onchange={(event) => emit({ description: event.currentTarget.value })} />
  </label>
</div>

<section class="weekly-section" aria-label="Date overrides">
  <h3>Date overrides</h3>
  <p class="detail-help">A date listed here uses these windows instead of its weekday's; a date with no windows is closed.</p>
  {#each model.dateOverrides as override, index (index)}
    <div class="override-row">
      <input
        type="date"
        value={override.date}
        disabled={readOnly}
        aria-label={`Override ${index + 1} date`}
        onchange={(event) => setOverride(index, { ...override, date: event.currentTarget.value })} />
      <div class="override-windows">
        {#if override.windows.length === 0}<span class="badge">closed</span>{/if}
        {@render windowsEditor(override.windows, override.date || `override ${index + 1}`, (next) => setOverride(index, { ...override, windows: next }))}
      </div>
      {#if !readOnly}
        <button type="button" class="iconly" aria-label={`Remove override ${index + 1}`} onclick={() => emit({ dateOverrides: model.dateOverrides.filter((_, i) => i !== index) })}>
          <Icon name="x" />
        </button>
      {/if}
    </div>
  {:else}
    {#if readOnly}<p class="dim">None.</p>{/if}
  {/each}
  {#if !readOnly}
    <button type="button" onclick={() => emit({ dateOverrides: [...model.dateOverrides, { date: '', windows: [] }] })}><Icon name="plus" />Date override</button>
  {/if}
</section>

<section class="weekly-section" aria-label="Excluded ranges">
  <h3>Excluded ranges</h3>
  <p class="detail-help">Subtracted last, whatever the weekday or override says: a closure, a freeze, a migration window.</p>
  {#each model.excludedRanges as range, index (index)}
    {@const dates = dateRange(range)}
    <div class="override-row">
      {#if dates}
        <input type="date" value={dates.from} disabled={readOnly} aria-label={`Excluded range ${index + 1} from`} onchange={(event) => setRange(index, { ...dates, from: event.currentTarget.value })} />
        <span class="window-to">to</span>
        <input type="date" value={dates.to} disabled={readOnly} aria-label={`Excluded range ${index + 1} to`} onchange={(event) => setRange(index, { ...dates, to: event.currentTarget.value })} />
      {:else if 'fromUtc' in range}
        <span class="mono">{range.fromUtc} to {range.toUtc}</span>
        <span class="dim">(instants; edit in JSON)</span>
      {/if}
      {#if !readOnly}
        <button type="button" class="iconly" aria-label={`Remove excluded range ${index + 1}`} onclick={() => emit({ excludedRanges: model.excludedRanges.filter((_, i) => i !== index) })}>
          <Icon name="x" />
        </button>
      {/if}
    </div>
  {:else}
    {#if readOnly}<p class="dim">None.</p>{/if}
  {/each}
  {#if !readOnly}
    <button type="button" onclick={() => emit({ excludedRanges: [...model.excludedRanges, { from: '', to: '' }] })}><Icon name="plus" />Date range</button>
  {/if}
</section>

<style>
  .week {
    display: grid;
    gap: 6px;
    margin: 8px 0 4px;
  }
  .week-scale,
  .week-row {
    display: grid;
    grid-template-columns: 3.4em minmax(0, 1fr);
    column-gap: 10px;
    align-items: center;
  }
  .week-scale { grid-template-areas: 'day bar'; }
  .week-row { grid-template-areas: 'day bar' '. windows'; }
  .week-ticks { grid-area: bar; }
  .week-day {
    grid-area: day;
    color: var(--muted);
    font-size: var(--text-sm);
    font-weight: 600;
    text-align: right;
  }
  .week-ticks {
    display: block;
    position: relative;
    height: 1.2em;
    color: var(--muted);
    font-size: var(--text-micro);
    font-family: var(--mono);
  }
  .tick { position: absolute; transform: translateX(-50%); }
  .tick:first-child { transform: none; }
  .tick:last-child { transform: translateX(-100%); }
  /* The bar is the editor's one loud element: each open window is a solid accent band on a 24-hour
     track, so a week reads as a picture before a single time is read. */
  .week-bar {
    grid-area: bar;
    position: relative;
    height: 22px;
    border-radius: 4px;
    background:
      repeating-linear-gradient(to right, transparent 0 calc(25% - 1px), var(--line) calc(25% - 1px) 25%),
      var(--panel-subtle);
    border: 1px solid var(--line);
    overflow: hidden;
  }
  .week-span {
    position: absolute;
    top: 0;
    bottom: 0;
    background: var(--accent-solid);
    opacity: 0.85;
  }
  .closed .week-bar { border-style: dashed; }
  .week-hours {
    position: absolute;
    right: 6px;
    top: 50%;
    transform: translateY(-50%);
    font-size: var(--text-micro);
    font-family: var(--mono);
    color: var(--ink);
    text-shadow: 0 0 4px var(--panel);
  }
  .closed .week-hours { color: var(--muted); }
  .week-windows { grid-area: windows; }
  .windows {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    align-items: center;
  }
  .window-chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 2px 4px 2px 6px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    font-size: var(--text-sm);
  }
  .window-chip.invalid { border-color: var(--bad); }
  .window-chip input[type='time'] {
    width: auto;
    padding: 2px 4px;
    border: 0;
    background: transparent;
    color: var(--ink);
    font: inherit;
    font-family: var(--mono);
  }
  .window-chip input[type='time']:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; border-radius: 3px; }
  .window-to { color: var(--muted); font-size: var(--text-xs); }
  .chip-remove { padding: 2px 4px; border: 0; }
  .window-add,
  .override-row > button { padding: 3px 8px; font-size: var(--text-sm); }
  .weekly-fields {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 0 20px;
  }
  .field-help { font-weight: 400; color: var(--muted); font-size: var(--text-xs); }
  .weekly-section { margin-top: 20px; }
  .weekly-section h3 {
    margin: 0 0 4px;
    font-size: var(--text-md);
    font-family: var(--font-display);
    font-weight: 600;
  }
  .weekly-section .detail-help { margin-bottom: 10px; }
  .override-row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
    padding: 6px 0;
    border-top: 1px solid color-mix(in srgb, var(--line) 60%, transparent);
  }
  .override-row + button,
  .weekly-section > button { margin-top: 8px; }
  .override-row input[type='date'] {
    padding: 4px 6px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
    font-family: var(--mono);
    font-size: var(--text-sm);
  }
  .override-windows {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
    flex: 1;
    min-width: 0;
  }
  @media (max-width: 640px) {
    .week-scale,
    .week-row { grid-template-columns: 2.6em minmax(0, 1fr); }
  }
</style>
