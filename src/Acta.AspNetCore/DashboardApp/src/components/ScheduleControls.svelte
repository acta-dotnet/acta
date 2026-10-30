<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import { previewSchedule, type ScheduleControlResponse, type SchedulePreview } from '../api.ts';
  import { scheduleControlMessage } from '../lib/controlMessages.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import { scheduleControlState } from './scheduleControlState.ts';
  import { displayFormatter, parseUtcDateTimeInput } from '../format.ts';
  import ConfirmAction from './ConfirmAction.svelte';
  import Icon from './Icon.svelte';
  import RelativeTime from './RelativeTime.svelte';

  let {
    jobNamespace,
    jobName,
    scheduleName,
    status,
    mode = 'all',
    onChanged = () => {}
  }: {
    jobNamespace: string;
    jobName: string;
    scheduleName: string;
    status: string;
    mode?: 'all' | 'editor' | 'actions';
    onChanged?: () => void;
  } = $props();

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  let message = $state('');
  let messageKind = $state('');
  let confirming = $state<'pause' | 'resume' | 'trigger' | null>(null);
  let pausingUntil = $state(false);
  let untilValue = $state('');

  // The server stays authoritative on legality; this only hides obviously inapplicable buttons.
  let { paused, canTrigger } = $derived(scheduleControlState(status));
  let showEditor = $derived(mode !== 'actions');
  let showActions = $derived(mode !== 'editor');

  // pause/resume/trigger all live at schedules/{ns}/{job}/{schedule}/{action}, share the note-only body
  // shape, and return ScheduleControlResponse; invalidating the 'schedules' key prefix refreshes every cached list.
  const mutation = useControlMutation<
    { action: string; reason?: string; extra?: Record<string, unknown> },
    ScheduleControlResponse
  >({
    path: (vars) => `schedules/${jobNamespace}/${jobName}/${scheduleName}/${vars.action}`,
    rawBody: (vars) => ({
      reasonMessage: vars.reason?.trim() || null,
      ...(vars.extra ?? {})
    }),
    notFound: () => ({ action: 'notFound' }),
    invalidateKeys: () => [['schedules']] as const
  });
  let busy = $derived(mutation.isPending);

  async function run(action: 'pause' | 'resume' | 'trigger', reason: string, extra?: Record<string, unknown>) {
    confirming = null;
    pausingUntil = false;
    message = '';
    try {
      const result = await mutation.mutateAsync({ action, reason, extra });
      // Rejected (e.g. the backend cleanly rejects a terminal-slot trigger) is a warning, never a
      // false success or a hard error - same guard as JobControls' `result.action === 'applied'`.
      message = scheduleControlMessage(action, result.action);
      messageKind = result.action === 'applied' ? 'ok' : 'warn';
      if (result.action === 'applied') onChanged();
    } catch (e) {
      message = (e as Error).message;
      messageKind = 'bad';
    }
  }

  function applyPauseUntil() {
    if (!parsedUntil.ok) {
      return;
    }
    run('pause', '', { pausedUntilUtc: parsedUntil.wire });
  }

  // Echo back both readings so the operator can see the UTC entry is not their local clock.
  let parsedUntil = $derived(parseUtcDateTimeInput(untilValue));
  let untilPreview = $derived(
    parsedUntil.ok
      ? { wire: parsedUntil.wire, local: displayFormatter.timestamp(parsedUntil.wire) }
      : null
  );

  // Preview next 10 runs: an on-demand read, not a polled query - the operator toggles it open,
  // fetches once, and closes it; reopening after a control action refetches (previewData reset).
  let previewOpen = $state(false);
  let previewData = $state<SchedulePreview | null>(null);
  let previewError = $state('');
  let previewLoading = $state(false);

  // nextOccurrencesUtc are UTC instants. The two views are the operator's IANA zone and raw UTC; the fixed
  // timestamp format includes both numeric offset and effective IANA zone.
  const browserZone = displayFormatter.localTimeZone;
  let previewZone = $state<'local' | 'utc'>('local');
  let displayZone = $derived(previewZone === 'utc' ? 'UTC' : browserZone);

  async function togglePreview() {
    previewOpen = !previewOpen;
    if (!previewOpen) {
      return;
    }
    previewLoading = true;
    previewError = '';
    try {
      previewData = await previewSchedule(jobNamespace, jobName, scheduleName);
    } catch (e) {
      previewError = (e as Error).message;
    } finally {
      previewLoading = false;
    }
  }
</script>

<div class="control-row">
  {#if showActions && canControlNow}
    {#if paused}
      <button disabled={busy} onclick={() => (confirming = 'resume')}><Icon name="play" />Resume</button>
    {:else}
      <button disabled={busy} onclick={() => (confirming = 'pause')}><Icon name="pause" />Pause</button>
      <button disabled={busy} onclick={() => (pausingUntil = !pausingUntil)}><Icon name="clock" />Pause until...</button>
    {/if}
    {#if canTrigger}
      <button disabled={busy} onclick={() => (confirming = 'trigger')}><Icon name="play" />Trigger now</button>
    {/if}
  {/if}
  {#if showEditor}
    <button onclick={togglePreview}><Icon name="clock" />{previewOpen ? 'Hide' : 'Preview'} next 10 runs</button>
  {/if}
</div>

{#if showActions && pausingUntil}
  <div class="until">
    <label class="until-field">
      <span class="until-text">Resume at</span>
      <input
        type="text"
        bind:value={untilValue}
        inputmode="numeric"
        autocomplete="off"
        placeholder="YYYY-MM-DD HH:mm[:ss]"
        aria-label="Resume at (UTC, YYYY-MM-DD HH:mm with optional seconds)"
        aria-invalid={untilValue !== '' && !parsedUntil.ok} />
      <span class="until-zone">UTC</span>
    </label>
    <button disabled={busy || !parsedUntil.ok} onclick={applyPauseUntil}>Apply</button>
    <button disabled={busy} onclick={() => (pausingUntil = false)}>Cancel</button>
  </div>
  {#if untilValue && !parsedUntil.ok}
    <div class="field-error" role="alert">{parsedUntil.error}</div>
  {/if}
  {#if untilPreview}
    <div class="until-preview dim">
      Wire instant <span class="mono">{untilPreview.wire}</span> · in your local time {untilPreview.local}
    </div>
  {/if}
{/if}

{#if showEditor && previewOpen}
  <div class="preview-panel">
    {#if previewLoading}
      <div class="dim">Loading preview...</div>
    {:else if previewError}
      <div class="control-message bad" role="status">{previewError}</div>
    {:else if previewData}
      <div class="preview-head">
        <span>Expression: <span class="mono">{previewData.expression}</span> · {previewData.timeZoneId}</span>
        <label class="preview-zone">
          Show in
          <select bind:value={previewZone} aria-label="Preview time zone">
            <option value="local">Browser local ({browserZone})</option>
            <option value="utc">UTC</option>
          </select>
        </label>
      </div>
      {#if previewData.nextOccurrencesUtc.length === 0}
        <div class="dim">No upcoming runs - the expression is exhausted.</div>
      {:else}
        <ol class="preview-runs">
          {#each previewData.nextOccurrencesUtc as occurrenceAtUtc}
            <li>
              <span class="mono preview-run-time">{displayFormatter.rowTimestampInZone(occurrenceAtUtc, displayZone)}</span>
              <span class="dim preview-run-relative"><RelativeTime value={occurrenceAtUtc} /></span>
            </li>
          {/each}
        </ol>
      {/if}
    {/if}
  </div>
{/if}

<style>
  .until {
    display: flex;
    gap: 8px;
    align-items: center;
    flex-wrap: wrap;
    margin-bottom: 12px;
  }
  .until-field {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    color: var(--muted);
  }
  .until-field input {
    padding: 5px 8px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
  }
  .until-field input:hover { border-color: var(--accent); }
  .until-field input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
  .until-zone {
    font-size: var(--text-sm);
    color: var(--muted);
    letter-spacing: 0.04em;
  }
  .until-preview {
    font-size: var(--text-sm);
    margin-bottom: 12px;
  }
  .field-error {
    color: var(--bad);
    font-size: var(--text-sm);
    margin: -6px 0 12px;
  }
  .preview-panel {
    margin-bottom: 12px;
    font-size: var(--text-sm);
  }
  .preview-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: 8px 16px;
    color: var(--muted);
    margin-bottom: 6px;
  }
  .preview-zone {
    display: inline-flex;
    align-items: center;
    gap: 6px;
  }
  .preview-zone select {
    padding: 4px 6px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
  }
  .preview-runs {
    margin: 0;
    padding: 0;
    list-style: none;
  }
  .preview-runs li {
    display: flex;
    align-items: baseline;
    gap: 12px;
    padding: 4px 0;
    border-bottom: 1px solid var(--line);
  }
  .preview-runs li:last-child {
    border-bottom: none;
  }
  .preview-run-time {
    min-width: 19ch;
  }
</style>

{#if message}
  <div class="control-message {messageKind}" role="status">{message}</div>
{/if}

{#if showActions && confirming === 'pause'}
  <ConfirmAction
    title={'Pause schedule ' + scheduleName + '?'}
    body="The schedule stops firing until resumed. Its cursor stays where it is, and a firing already running finishes."
    confirmLabel="Pause schedule"
    requireReason={true}
    onConfirm={(note) => run('pause', note)}
    onCancel={() => (confirming = null)} />
{/if}

{#if showActions && confirming === 'resume'}
  <ConfirmAction
    title={'Resume schedule ' + scheduleName + '?'}
    body="The schedule fires again from its kept cursor; the occurrences it missed while paused are decided by its misfire policy at the next pass."
    confirmLabel="Resume schedule"
    requireReason={true}
    onConfirm={(note) => run('resume', note)}
    onCancel={() => (confirming = null)} />
{/if}

{#if showActions && confirming === 'trigger'}
  <ConfirmAction
    title={'Trigger schedule ' + scheduleName + ' now?'}
    body="Runs the job once now, outside the schedule's cadence, and leaves the cursor where it is. A recurring job must be idle with no firing outstanding, and a per-occurrence schedule must be under its limit, or the trigger is rejected."
    confirmLabel="Trigger now"
    requireReason={true}
    onConfirm={(note) => run('trigger', note)}
    onCancel={() => (confirming = null)} />
{/if}
