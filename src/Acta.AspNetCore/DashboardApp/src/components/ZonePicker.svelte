<script module>
  // Per-instance ids for the aria wiring: job detail renders one control set per schedule.
  let seq = 0;
</script>

<script lang="ts">
  import { tick } from 'svelte';
  import { filterZones, suggestedTimeZones } from '../lib/timeZones.ts';

  // A DOM listbox rather than a select: a native popup paints with UA colors before the page theme
  // reaches it, which shows as a dark flash on the light themes, and 400+ options make that first
  // paint slow enough to see. Typing filters, so a zone is a few keystrokes rather than a long scroll.
  let { value = $bindable(''), disabled = false }: { value?: string; disabled?: boolean } = $props();

  const uid = 'zone-' + seq++;
  const listId = uid + '-list';

  let open = $state(false);
  let query = $state('');
  let active = $state(0);
  let boxEl: HTMLDivElement | null = $state(null);
  let inputEl: HTMLInputElement | null = $state(null);

  // The tzdb list is only needed once the picker opens.
  let zones = $derived(open ? suggestedTimeZones() : []);
  let matches = $derived(filterZones(zones, query));

  $effect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (boxEl && !boxEl.contains(event.target as Node)) close();
    };
    window.addEventListener('pointerdown', onPointerDown);
    return () => window.removeEventListener('pointerdown', onPointerDown);
  });

  function openList(): void {
    if (disabled) return;
    open = true;
    query = '';
    active = 0;
  }

  function close(): void {
    open = false;
    query = '';
  }

  function choose(zone: string): void {
    value = zone;
    close();
    inputEl?.focus();
  }

  async function move(delta: number): Promise<void> {
    if (!open) {
      openList();
      return;
    }
    active = Math.min(Math.max(active + delta, 0), matches.length);
    await tick();
    boxEl?.querySelector<HTMLElement>('[data-active="true"]')?.scrollIntoView({ block: 'nearest' });
  }

  function onKeydown(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      move(event.key === 'ArrowDown' ? 1 : -1);
      return;
    }
    if (event.key === 'Escape' && open) {
      event.preventDefault();
      event.stopPropagation();
      close();
      return;
    }
    if (event.key === 'Enter' && open) {
      event.preventDefault();
      // Row 0 is the inherit row; the zones follow it.
      if (active === 0) choose('');
      else if (matches[active - 1]) choose(matches[active - 1]);
    }
  }
</script>

<div class="zone-picker" bind:this={boxEl}>
  <input
    bind:this={inputEl}
    class="zone-input"
    type="text"
    role="combobox"
    aria-expanded={open}
    aria-controls={listId}
    aria-autocomplete="list"
    aria-label="Time zone override"
    autocomplete="off"
    spellcheck="false"
    {disabled}
    placeholder={value === '' ? 'No override (inherit)' : ''}
    value={open ? query : value}
    onfocus={openList}
    oninput={(event) => {
      query = event.currentTarget.value;
      open = true;
      active = 0;
    }}
    onkeydown={onKeydown} />

  {#if open}
    <ul class="zone-list" id={listId} role="listbox">
      <li
        role="option"
        aria-selected={value === ''}
        data-active={active === 0}
        class="zone-option inherit"
        class:active={active === 0}
        onpointerdown={(event) => { event.preventDefault(); choose(''); }}>
        No override (inherit)
      </li>
      {#each matches as zone, index}
        <li
          role="option"
          aria-selected={value === zone}
          data-active={active === index + 1}
          class="zone-option"
          class:active={active === index + 1}
          onpointerdown={(event) => { event.preventDefault(); choose(zone); }}>
          {zone}
        </li>
      {/each}
      {#if matches.length === 0}
        <li class="zone-empty">No zone matches "{query}"</li>
      {/if}
    </ul>
  {/if}
</div>

<style>
  .zone-picker { position: relative; display: inline-block; }
  .zone-input {
    width: 220px;
    padding: 5px 8px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
  }
  .zone-input:hover { border-color: var(--accent); }
  .zone-input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
  .zone-list {
    position: absolute;
    z-index: 20;
    top: calc(100% + 4px);
    left: 0;
    width: 260px;
    max-height: 240px;
    overflow-y: auto;
    margin: 0;
    padding: 4px;
    list-style: none;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    box-shadow: 0 8px 20px rgb(0 0 0 / 0.18);
  }
  .zone-option {
    padding: 5px 8px;
    border-radius: var(--radius-control);
    color: var(--ink);
    cursor: pointer;
  }
  .zone-option.inherit { color: var(--muted); }
  .zone-option:hover,
  .zone-option.active { background: var(--nav-active-bg); }
  .zone-empty { padding: 6px 8px; color: var(--muted); }
</style>
