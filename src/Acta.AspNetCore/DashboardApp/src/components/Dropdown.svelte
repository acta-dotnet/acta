<script module>
  // Per-instance ids for the aria wiring: a page can render several of these.
  let seq = 0;
</script>

<script lang="ts">
  import { tick } from 'svelte';

  // A DOM listbox rather than a native <select>. Two reasons, both visible to users: the OS-drawn
  // popup paints with UA colors before the page theme reaches it, which reads as a white sheet on the
  // dark themes; and a long list is a scroll rather than a few keystrokes. Typing filters once the
  // list is longer than `typeaheadFrom`, so short enum-shaped lists stay plain.
  //
  // Short enum filters should keep using a native <select> - this is for the long, data-driven lists
  // (namespaces, job names). ZonePicker stays its own component: it lazy-loads the tzdb and does
  // zone-specific matching, which is not worth generalising into here.
  type Option = { value: string; label: string };

  let {
    options = [],
    value = $bindable(''),
    label,
    placeholder = 'Select',
    disabled = false,
    typeaheadFrom = 8,
    onchange = undefined,
  }: {
    options?: Option[];
    value?: string;
    label: string;
    placeholder?: string;
    disabled?: boolean;
    typeaheadFrom?: number;
    onchange?: (value: string) => void;
  } = $props();

  const uid = 'dd-' + seq++;
  const listId = uid + '-list';

  let open = $state(false);
  let query = $state('');
  let rootEl: HTMLDivElement | null = $state(null);
  let triggerEl: HTMLButtonElement | null = $state(null);
  let inputEl: HTMLInputElement | null = $state(null);
  let optionEls: HTMLButtonElement[] = $state([]);

  let typeahead = $derived(options.length >= typeaheadFrom);
  let matches = $derived(
    query.trim().length === 0
      ? options
      : options.filter((o) => o.label.toLowerCase().includes(query.trim().toLowerCase()))
  );
  let selectedLabel = $derived(options.find((o) => o.value === value)?.label ?? placeholder);

  async function openList(): Promise<void> {
    if (disabled) return;
    open = true;
    query = '';
    await tick();
    if (typeahead) inputEl?.focus();
    else optionEls[Math.max(matches.findIndex((o) => o.value === value), 0)]?.focus();
  }

  function close(restoreFocus: boolean): void {
    if (!open) return;
    open = false;
    if (restoreFocus) triggerEl?.focus();
  }

  function choose(next: string): void {
    value = next;
    onchange?.(next);
    close(true);
  }

  function onListKeyDown(event: KeyboardEvent): void {
    const index = optionEls.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      optionEls[Math.min(index + 1, matches.length - 1)]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (index <= 0 && typeahead) inputEl?.focus();
      else optionEls[Math.max(index - 1, 0)]?.focus();
    } else if (event.key === 'Home') {
      event.preventDefault();
      optionEls[0]?.focus();
    } else if (event.key === 'End') {
      event.preventDefault();
      optionEls[matches.length - 1]?.focus();
    }
  }

  function onInputKeyDown(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      optionEls[0]?.focus();
    } else if (event.key === 'Enter' && matches.length > 0) {
      event.preventDefault();
      choose(matches[0].value);
    }
  }

  function onWindowPointerDown(event: PointerEvent): void {
    if (open && rootEl && event.target instanceof Node && !rootEl.contains(event.target)) close(false);
  }

  function onWindowKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && open) {
      event.preventDefault();
      close(true);
    }
  }

  // Close when focus leaves the control entirely - tabbing out of the last option should not leave an
  // orphaned popover floating over the page. relatedTarget is null when focus goes to the window.
  function onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget;
    if (!open) return;
    if (next instanceof Node && rootEl?.contains(next)) return;
    if (next === null) return;
    close(false);
  }
</script>

<svelte:window onpointerdown={onWindowPointerDown} onkeydown={onWindowKeyDown} />

<div class="dropdown" bind:this={rootEl} onfocusout={onFocusOut}>
  <button
    type="button"
    class="trigger"
    bind:this={triggerEl}
    {disabled}
    aria-haspopup="listbox"
    aria-expanded={open}
    aria-controls={listId}
    aria-label={label}
    title={selectedLabel}
    onclick={() => (open ? close(true) : openList())}>
    {selectedLabel}
  </button>
  {#if open}
    <div id={listId} class="listbox" role="listbox" tabindex="-1" aria-label={label} onkeydown={onListKeyDown}>
      {#if typeahead}
        <input
          class="filter"
          type="text"
          bind:this={inputEl}
          bind:value={query}
          placeholder="Filter"
          aria-label={label + ' filter'}
          onkeydown={onInputKeyDown} />
      {/if}
      {#each matches as option, index (option.value)}
        <button
          type="button"
          role="option"
          class="option"
          class:selected={value === option.value}
          bind:this={optionEls[index]}
          aria-selected={value === option.value}
          onclick={() => choose(option.value)}>
          {option.label}
        </button>
      {/each}
      {#if matches.length === 0}
        <div class="empty">No matches</div>
      {/if}
    </div>
  {/if}
</div>

<style>
  .dropdown { position: relative; }

  .listbox {
    position: absolute;
    left: 0;
    right: 0;
    top: calc(100% + 4px);
    z-index: 20;
    max-height: min(48dvh, 420px);
    overflow-y: auto;
    overscroll-behavior: contain;
    padding: 4px;
    display: flex;
    flex-direction: column;
    background: var(--panel);
    border: 1px solid var(--line);
    border-radius: var(--radius-panel);
    box-shadow: 0 8px 30px var(--shadow);
  }

  .filter {
    margin: 2px 2px 6px;
    padding: 6px 8px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel-subtle);
    color: var(--ink);
    font: inherit;
  }

  .option {
    padding: 7px 8px;
    border: 0;
    border-radius: var(--radius-control);
    background: transparent;
    color: var(--ink);
    font: inherit;
    font-family: var(--mono);
    text-align: left;
    cursor: pointer;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .option:hover { background: var(--panel-subtle); }
  .option.selected { color: var(--accent); font-weight: 600; }

  .empty { padding: 7px 8px; color: var(--ink-dim); }
</style>
