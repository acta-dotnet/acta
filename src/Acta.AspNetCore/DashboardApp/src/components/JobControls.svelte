<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import type { JobControlResponse } from '../api.ts';
  import { displayFormatter, parseUtcDateTimeInput } from '../format.ts';
  import { jobControlState } from './jobControlState.ts';
  import ConfirmAction from './ConfirmAction.svelte';
  import Icon from './Icon.svelte';

  let { jobRef, status, priority = null, embedded = false, onChanged = () => {} }: {
    jobRef: string;
    status: string;
    priority?: string | null;
    embedded?: boolean;
    onChanged?: () => void;
  } = $props();

  let message = $state('');
  let messageKind = $state('');
  let confirming = $state<string | null>(null);
  let reschedulingOpen = $state(false);
  let reprioritizingOpen = $state(false);
  let nextRunAtUtcInput = $state('');
  let rescheduleWireToConfirm = $state<string | null>(null);
  let newPriority = $state('normal');

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  const priorities = ['bulk', 'normal', 'high', 'critical', 'realtime'];

  // The server stays authoritative on legality; this only hides obviously inapplicable buttons.
  let { canPause, canResume, canRestart, canCancel, canReschedule, canReprioritize, canPurge } = $derived(
    jobControlState(status)
  );

  const text: Record<
    string,
    { title: string; body: string; label: string; danger: boolean; requireReason: boolean; warning: string }
  > = {
    pause: {
      title: 'Pause job?',
      body: 'The job stays persisted but is skipped by claims until resumed.',
      label: 'Pause job',
      danger: false,
      requireReason: false,
      warning: ''
    },
    resume: {
      title: 'Resume job?',
      body: 'The job becomes claimable again.',
      label: 'Resume job',
      danger: false,
      requireReason: false,
      warning: ''
    },
    restart: {
      title: 'Run this job again?',
      body: 'Re-arms the same job id to run again from its persisted input. Prior attempts and events stay in the audit timeline.',
      label: 'Run again',
      danger: true,
      requireReason: true,
      warning: 'This job may execute again. External side effects performed by an earlier attempt may be repeated.'
    },
    cancel: {
      title: 'Cancel job?',
      body: 'Marks this job cancelled if the transition is legal and cascades to its non-terminal descendants. Already-finished descendants are unaffected.',
      label: 'Cancel job',
      danger: true,
      requireReason: true,
      warning: ''
    },
    reschedule: {
      title: 'Change next execution time?',
      body: 'Moves the next-run instant. A paused job is re-armed ready so the new time actually fires.',
      label: 'Change run time',
      danger: false,
      requireReason: false,
      warning: ''
    },
    reprioritize: {
      title: 'Change job priority?',
      body: 'Applies to the next claim only; an in-flight attempt (if any) is unaffected.',
      label: 'Change priority',
      danger: false,
      requireReason: false,
      warning: ''
    },
    purge: {
      title: 'Permanently delete this job record?',
      body: "Permanently deletes this job's events, alerts, and child rows (runtime, schedule, steps, results, checkpoints, tags), then the job row itself. A terminal job with child jobs cannot be purged - that would orphan their lineage.",
      label: 'Permanently delete record',
      danger: true,
      requireReason: false,
      warning: 'This permanently removes the job and its entire history. It cannot be undone.'
    }
  };

  // Echo back both readings so the operator can see the UTC entry is not their local clock -
  // same pattern as ScheduleControls' pause-until preview.
  let parsedReschedule = $derived(parseUtcDateTimeInput(nextRunAtUtcInput));
  let reschedulePreview = $derived(
    parsedReschedule.ok
      ? { wire: parsedReschedule.wire, local: displayFormatter.timestamp(parsedReschedule.wire) }
      : null
  );
  let rescheduleBody = $derived(
    rescheduleWireToConfirm
      ? `Moves the next-run instant to ${displayFormatter.timestamp(rescheduleWireToConfirm)} (UTC ${rescheduleWireToConfirm}). A paused job is re-armed ready so the new time actually fires.`
      : text.reschedule.body
  );
  let reprioritizeBody = $derived(`Changes claim priority to "${newPriority}". ${text.reprioritize.body}`);

  function openReprioritize() {
    newPriority = priority ?? 'normal';
    reprioritizingOpen = true;
  }

  function continueReschedule() {
    if (!parsedReschedule.ok) return;
    rescheduleWireToConfirm = parsedReschedule.wire;
    confirming = 'reschedule';
  }

  function cancelConfirmation() {
    confirming = null;
    rescheduleWireToConfirm = null;
  }

  export function openAction(action: string): void {
    if (!canControlNow) return;
    const legal = {
      pause: canPause,
      resume: canResume,
      restart: canRestart,
      cancel: canCancel,
      reschedule: canReschedule,
      reprioritize: canReprioritize,
      purge: canPurge
    }[action];
    if (!legal) return;
    if (action === 'reschedule') reschedulingOpen = true;
    else if (action === 'reprioritize') openReprioritize();
    else confirming = action;
  }

  // All seven verbs live at jobs/{jobRef}/{action} and return JobControlResponse; invalidating the
  // single-element 'jobs' key prefix covers both the job-detail query (['jobs','detail',ref]) and
  // every jobs-list query (['jobs', filters]) - the same prefix-match trick JobDetail.svelte's own
  // reload() uses for the events key.
  const mutation = useControlMutation<
    { jobRef: string; action: string; reason?: string; extra?: Record<string, unknown> },
    JobControlResponse
  >({
    path: (vars) => `jobs/${vars.jobRef}/${vars.action}`,
    body: (vars) => vars.extra ?? {},
    notFound: (vars) => ({ jobRef: vars.jobRef, action: 'notFound', status: null, message: 'Job not found.' }),
    invalidateKeys: () => [['jobs']] as const
  });
  let busy = $derived(mutation.isPending);

  async function run(action: string, reason: string, extra?: Record<string, unknown>) {
    confirming = null;
    rescheduleWireToConfirm = null;
    reschedulingOpen = false;
    reprioritizingOpen = false;
    message = '';
    try {
      const result = await mutation.mutateAsync({ jobRef, action, reason, extra });
      message = result.message;
      messageKind = result.action === 'applied' ? 'ok' : 'warn';
      onChanged();
    } catch (e) {
      message = (e as Error).message;
      messageKind = 'bad';
    }
  }

  function confirmAction(reason: string) {
    const action = confirming;
    if (!action) return;

    if (action === 'reschedule') {
      const nextRunAtUtc = rescheduleWireToConfirm;
      if (!nextRunAtUtc) {
        cancelConfirmation();
        return;
      }
      void run(action, reason, { nextRunAtUtc });
      return;
    }

    void run(action, reason, action === 'reprioritize' ? { priority: newPriority } : undefined);
  }
</script>

<section class:panel={!embedded} class:detail-panel={embedded}>
  <h2>Actions</h2>
  <div class="control-row">
    {#if canControlNow}
      {#if canPause}<button disabled={busy} onclick={() => openAction('pause')}><Icon name="pause" />Pause</button>{/if}
      {#if canResume}<button disabled={busy} onclick={() => openAction('resume')}><Icon name="play" />Resume</button>{/if}
      {#if canRestart}<button disabled={busy} onclick={() => openAction('restart')}><Icon name="reload" />Run again</button>{/if}
      {#if canReschedule}<button disabled={busy} onclick={() => openAction('reschedule')}><Icon name="clock" />Change run time</button>{/if}
      {#if canReprioritize}<button disabled={busy} onclick={() => openAction('reprioritize')}><Icon name="chevron-right" />Change priority</button>{/if}
      {#if canCancel}<button class="danger-outline" disabled={busy} onclick={() => openAction('cancel')}><Icon name="x" />Cancel job</button>{/if}
      {#if canPurge}<button class="danger-outline" disabled={busy} onclick={() => openAction('purge')}><Icon name="x-circle" />Delete record</button>{/if}
    {:else}
      <span class="dim">Job controls are disabled on this host.</span>
    {/if}
  </div>

  {#if reschedulingOpen}
    <div class="until">
      <label class="until-field">
        <span class="until-text">Next run at</span>
        <input
          type="text"
          bind:value={nextRunAtUtcInput}
          inputmode="numeric"
          autocomplete="off"
          placeholder="YYYY-MM-DD HH:mm[:ss]"
          aria-label="Next run at (UTC, YYYY-MM-DD HH:mm with optional seconds)"
          aria-invalid={nextRunAtUtcInput !== '' && !parsedReschedule.ok} />
        <span class="until-zone">UTC</span>
      </label>
      <button disabled={!parsedReschedule.ok} onclick={continueReschedule}>Continue</button>
      <button onclick={() => { reschedulingOpen = false; nextRunAtUtcInput = ''; rescheduleWireToConfirm = null; }}>Cancel</button>
    </div>
    {#if nextRunAtUtcInput && !parsedReschedule.ok}
      <div class="field-error" role="alert">{parsedReschedule.error}</div>
    {/if}
    {#if reschedulePreview}
      <div class="until-preview dim">
        Wire instant <span class="mono">{reschedulePreview.wire}</span> · in your local time {reschedulePreview.local}
      </div>
    {/if}
  {/if}

  {#if reprioritizingOpen}
    <div class="until">
      <label class="until-field">
        <span class="until-text">New priority</span>
        <select bind:value={newPriority}>
          {#each priorities as p}<option value={p}>{p}</option>{/each}
        </select>
      </label>
      <button onclick={() => (confirming = 'reprioritize')}>Continue</button>
      <button onclick={() => (reprioritizingOpen = false)}>Cancel</button>
    </div>
  {/if}

  {#if message}
    <div class="control-message {messageKind}" role="status">{message}</div>
  {/if}
</section>

{#if confirming}
  <ConfirmAction
    title={text[confirming].title.replace('job?', 'job ' + jobRef + '?')}
    body={confirming === 'reschedule' ? rescheduleBody : confirming === 'reprioritize' ? reprioritizeBody : text[confirming].body}
    confirmLabel={text[confirming].label}
    danger={text[confirming].danger}
    requireReason={text[confirming].requireReason}
    showReason={confirming !== 'purge'}
    warning={text[confirming].warning}
    confirmPhrase={confirming === 'purge' ? jobRef : ''}
    onConfirm={confirmAction}
    onCancel={cancelConfirmation} />
{/if}

<style>
  .until {
    display: flex;
    gap: 8px;
    align-items: center;
    flex-wrap: wrap;
    margin-top: 12px;
  }
  .until-field {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    color: var(--muted);
  }
  .until-field input,
  .until-field select {
    padding: 5px 8px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
  }
  .until-field input:hover,
  .until-field select:hover { border-color: var(--accent); }
  .until-field input:focus-visible,
  .until-field select:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
  .until-zone {
    font-size: var(--text-sm);
    color: var(--muted);
    letter-spacing: 0.04em;
  }
  .until-preview {
    font-size: var(--text-sm);
    margin-top: 6px;
  }
  .field-error {
    color: var(--bad);
    font-size: var(--text-sm);
    margin-top: 6px;
  }
</style>
