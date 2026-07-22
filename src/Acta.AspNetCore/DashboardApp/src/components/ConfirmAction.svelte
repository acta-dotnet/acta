<script module>
  // Per-instance unique ids for aria wiring (no Math.random: a module counter is deterministic).
  let seq = 0;
</script>

<script lang="ts">
  import { tick } from 'svelte';

  interface Props {
    title: string;
    body?: string;
    confirmLabel?: string;
    requireReason?: boolean;
    showReason?: boolean;
    danger?: boolean;
    confirmPhrase?: string;
    warning?: string;
    onConfirm?: (reason: string) => void;
    onCancel?: () => void;
  }

  let {
    title,
    body = '',
    confirmLabel = 'Confirm',
    requireReason = false,
    showReason = true,
    danger = false,
    /** When set, the operator must type this exact phrase (e.g. the job ref or "DELETE") to enable confirm. */
    confirmPhrase = '',
    /** Optional prominent warning shown above the reason (e.g. the at-least-once notice). */
    warning = '',
    onConfirm = (_reason: string) => {},
    onCancel = () => {}
  }: Props = $props();

  const uid = 'confirm-' + seq++;
  const titleId = uid + '-title';
  const bodyId = uid + '-body';

  let reason = $state('');
  let typed = $state('');
  let submitting = $state(false);

  let boxEl: HTMLDivElement | null = $state(null);
  let reasonEl: HTMLTextAreaElement | null = $state(null);
  let cancelEl: HTMLButtonElement | null = $state(null);
  let confirmEl: HTMLButtonElement | null = $state(null);
  let opener: HTMLElement | null = null;

  let phraseOk = $derived(confirmPhrase === '' || typed.trim() === confirmPhrase);
  let blocked = $derived(submitting || (requireReason && reason.trim().length === 0) || !phraseOk);

  // Focus management: capture the opener, move focus into the dialog (the reason textarea when
  // shown, else Cancel for destructive actions so the safe choice is default), and restore focus
  // to the opener when the dialog closes.
  $effect(() => {
    opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    tick().then(() => (reasonEl ?? (danger ? cancelEl : confirmEl))?.focus());
    return () => opener?.focus?.();
  });

  function focusables(): HTMLElement[] {
    if (!boxEl) return [];
    return [...boxEl.querySelectorAll<HTMLElement>('button, input, select, textarea, [href], [tabindex]:not([tabindex="-1"])')]
      .filter((el) => !(el instanceof HTMLButtonElement || el instanceof HTMLInputElement || el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement) || !el.disabled)
      .filter((el) => el.offsetParent !== null);
  }

  // Trap Tab/Shift+Tab inside the box, and handle Escape here (stopping propagation so a parent
  // window handler does not double-fire).
  function onKeydown(e: KeyboardEvent): void {
    if (e.key === 'Escape') {
      e.stopPropagation();
      if (!submitting) onCancel();
      return;
    }
    if (e.key !== 'Tab') return;
    const f = focusables();
    if (f.length === 0) return;
    const first = f[0];
    const last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  function confirm(): void {
    if (blocked) return;
    submitting = true; // guards against a double-click firing the action twice
    onConfirm(reason.trim());
  }
</script>

<div class="confirm-overlay" role="presentation" onkeydown={onKeydown}>
  <div
    class="confirm-box"
    role="dialog"
    aria-modal="true"
    aria-labelledby={titleId}
    aria-describedby={body ? bodyId : undefined}
    bind:this={boxEl}>
    <h3 id={titleId}>{title}</h3>
    {#if body}
      <p id={bodyId}>{body}</p>
    {/if}
    {#if warning}
      <p class="confirm-warning" role="note">{warning}</p>
    {/if}

    {#if showReason}
      <label class="control-reason">
        {requireReason ? 'Reason (required, recorded in job events)' : 'Reason (optional, recorded in job events)'}
        <textarea bind:this={reasonEl} rows="2" maxlength="512" bind:value={reason} disabled={submitting}></textarea>
      </label>
    {/if}

    {#if confirmPhrase}
      <label class="control-reason">
        Type <span class="mono">{confirmPhrase}</span> to confirm
        <input class="confirm-phrase" bind:value={typed} disabled={submitting} autocomplete="off" spellcheck="false" />
      </label>
    {/if}

    <div class="confirm-actions">
      <button bind:this={cancelEl} disabled={submitting} onclick={() => onCancel()}>Keep as is</button>
      <button bind:this={confirmEl} class:danger-outline={danger} disabled={blocked} onclick={confirm}>
        {submitting ? 'Working…' : confirmLabel}
      </button>
    </div>
  </div>
</div>

<style>
  .confirm-warning {
    margin: 0 0 10px;
    padding: 8px 10px;
    border-radius: var(--radius-control);
    background: var(--badge-warn-bg);
    color: var(--warn);
    font-size: var(--text-sm);
  }
  .confirm-phrase {
    width: 100%;
    box-sizing: border-box;
    padding: 6px 10px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
  }
  .confirm-phrase:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 1px;
  }
</style>
